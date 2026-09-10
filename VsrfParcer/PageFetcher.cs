using Microsoft.Playwright;

namespace VsrfParcer
{
    /// <summary>
    /// Получение HTML-страниц и файлов через настоящий headless-браузер (Playwright/
    /// Chromium) вместо прямого HTTP-запроса — сайт vsrf.ru блокирует не-браузерные
    /// запросы (200 с пустым телом), и обычные HTTP-заголовки это не обходят.
    ///
    /// ВАЖНО: этот файл запускает Playwright/Chromium НА ТОЙ ЖЕ машине, где работает
    /// VsrfParcer.exe. Поэтому весь проект целиком должен запускаться на современной
    /// Windows-машине (10/11/Server 2019+) по расписанию в 10:00 — а не на старом
    /// сервере (Windows Server 2012 R2), где современный Node.js/Chromium физически
    /// не запускаются. См. README про перенос запуска и общую папку для PDF.
    ///
    /// Браузер запускается один раз и переиспользуется между запросами.
    /// </summary>
    internal static class PageFetcher
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        private static IPlaywright? _playwright;
        private static IBrowser? _browser;
        private static readonly SemaphoreSlim InitLock = new(1, 1);

        private static async Task<IBrowser> EnsureBrowserAsync()
        {
            if (_browser is not null)
                return _browser;

            await InitLock.WaitAsync();
            try
            {
                if (_browser is not null)
                    return _browser;

                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true
                });
                return _browser;
            }
            finally
            {
                InitLock.Release();
            }
        }

        /// <summary>
        /// Открывает страницу как настоящий браузер и возвращает её итоговый HTML
        /// (после выполнения JS, если сайт что-то дорисовывает) и HTTP-статус
        /// основного ответа.
        /// </summary>
        public static async Task<(string Html, int Status)> GetHtmlAsync(string url)
        {
            var browser = await EnsureBrowserAsync();
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = UserAgent,
                Locale = "ru-RU"
            });

            try
            {
                var page = await context.NewPageAsync();

                IResponse? response;
                try
                {
                    response = await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.NetworkIdle,
                        Timeout = 30000
                    });
                }
                catch
                {
                    // NetworkIdle иногда не наступает из-за фоновых запросов
                    // (аналитика, вебсокеты и т.п.) — пробуем ещё раз с менее
                    // строгим условием ожидания вместо того, чтобы падать.
                    response = await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Load,
                        Timeout = 30000
                    });
                }

                var html = await page.ContentAsync();
                var status = response?.Status ?? 0;
                return (html, status);
            }
            finally
            {
                await context.CloseAsync();
            }
        }

        /// <summary>
        /// Скачивает файл (например, PDF) в рамках того же браузерного контекста,
        /// что сначала открыл referrerUrl — так запрос на файл несёт те же куки/
        /// сессию, которые сайт мог выставить как часть анти-бот-проверки при
        /// первом заходе на страницу дела.
        ///
        /// ВАЖНО (история): изначально файл скачивался через
        /// context.APIRequest.GetAsync(...) — это отдельный лёгкий HTTP-клиент
        /// внутри Playwright, а не сам браузер, и на практике он не резолвил DNS
        /// ("getaddrinfo ENOTFOUND www.vsrf.ru", хотя обычная навигация на тот же
        /// домен парой секунд раньше отрабатывала нормально — похоже, дело в
        /// прокси, который Chromium подхватывает из настроек ОС, а APIRequest — нет).
        /// Затем попробовали обычную навигацию (page.GotoAsync) — но выяснилось,
        /// что сайт отдаёт PDF как вложение (Content-Disposition: attachment), и
        /// Chromium в ответ на такую навигацию не возвращает Response, а сразу
        /// инициирует скачивание файла, бросая исключение "Download is starting"
        /// вместо завершения навигации. Поэтому теперь используется именно
        /// обработка события скачивания (page.WaitForDownloadAsync), а не чтение
        /// тела ответа напрямую.
        /// </summary>
        public static async Task<byte[]?> DownloadFileAsync(string fileUrl, string referrerUrl)
        {
            var browser = await EnsureBrowserAsync();
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = UserAgent,
                Locale = "ru-RU",
                AcceptDownloads = true
            });

            try
            {
                var page = await context.NewPageAsync();
                await page.GotoAsync(referrerUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 30000
                });

                var downloadTask = page.WaitForDownloadAsync();
                try
                {
                    await page.GotoAsync(fileUrl, new PageGotoOptions { Timeout = 30000 });
                }
                catch
                {
                    // Ожидаемо: навигация на файл-вложение всегда бросает исключение
                    // ("Download is starting") — сам файл ловим ниже через downloadTask,
                    // это не признак настоящей ошибки.
                }

                var completed = await Task.WhenAny(downloadTask, Task.Delay(30000));
                if (completed != downloadTask)
                    return null; // скачивание не началось за разумное время

                var download = await downloadTask;
                var tempPath = await download.PathAsync();
                return tempPath is null ? null : await File.ReadAllBytesAsync(tempPath);
            }
            finally
            {
                await context.CloseAsync();
            }
        }

        /// <summary>Закрыть браузер. Вызывается один раз перед завершением программы.</summary>
        public static async Task ShutdownAsync()
        {
            if (_browser is not null)
                await _browser.CloseAsync();
            _playwright?.Dispose();
        }
    }
}

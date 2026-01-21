using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Io;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.RegularExpressions;
using VsrfParcer.AppContext;
using VsrfParcer.Models;

namespace VsrfParcer
{
    internal class Parcer
    {
        private const string VsrfAddress = "https://vsrf.ru";

        public static async Task ParceNewCasesAsync(RepealesContext repealesContext)
        {
            var config = Configuration.Default.WithDefaultLoader();
            var context = BrowsingContext.New(config);
            string url = "https://vsrf.ru/lk/practice/acts?&numberExact=off&number=-%D0%9A4&actDateExact=off&actNewArrival=WEEK";
            var doc = await context.OpenAsync(url);
            if (doc is not null)
            {
                Logger.AddLog($"Страница {url} открыта, перебор карточек");
                foreach (var e in doc.GetElementsByClassName("vs-items-label"))
                {
                    var a = e.GetElementsByTagName("a").First();
                    var caseUrl = VsrfAddress + a.GetAttribute("href");
                    Logger.AddLog($"Обрабатывается страница {caseUrl}");
                    await ParceCaseAsync(caseUrl, repealesContext);
                }
            }
            else
                Logger.AddLog("Не удалось открыть страницу");
        }

        public static async Task ParcePdfsAsync(Case caseToParce, RepealesContext repealesContext)
        {
            if (caseToParce.Link is null)
            {
                Logger.AddLog($"Нет ссылки для карточки {caseToParce.VsrfNumber}, попытка найти ссылку");
                if (!await ParceNewLinkAsync(caseToParce, repealesContext))
                {
                    Logger.AddLog($"Новая ссылка для карточки {caseToParce.VsrfNumber} не найдена");
                    return;
                }
            }
            var config = Configuration.Default.WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = true });
            var context = BrowsingContext.New(config);
            var doc = await context.OpenAsync(caseToParce.Link);
            if (doc is not null)
            {
                Logger.AddLog($"Страница {caseToParce.Link} для карточки {caseToParce.VsrfNumber} открыта, поиск ссылки на pdf");
                var cards = doc.GetElementsByClassName("scroll-target");
                if (cards.Length == 0)
                {
                    Logger.AddLog($"Ссылка на pdf для карточки {caseToParce.VsrfNumber} не найдена, проверка актуальности ссылки на карточку");
                    if (!await ParceNewLinkAsync(caseToParce, repealesContext))
                    {
                        Logger.AddLog($"Новая ссылка для карточки {caseToParce.VsrfNumber} не найдена");
                        return;
                    }
                }
                var card = cards.First();
                var links = card.GetElementsByClassName("vs-item-icon-link");
                if (links.Length == 0)
                {
                    Logger.AddLog($"Ссылка на pdf для карточки {caseToParce.VsrfNumber} не найдена, проверка актуальности ссылки на карточку");
                    if (!await ParceNewLinkAsync(caseToParce, repealesContext))
                    {
                        Logger.AddLog($"Новая ссылка для карточки {caseToParce.VsrfNumber} не найдена");
                        return;
                    }
                }
                var a = links.First();
                string pdfUrl = VsrfAddress + a.GetAttribute("href").Trim();
                Logger.AddLog($"Ссылка на pdf для карточки {caseToParce.VsrfNumber}: {pdfUrl}");
                if (pdfUrl == VsrfAddress)
                {
                    Logger.AddLog($"Ссылка на pdf для карточки {caseToParce.VsrfNumber} не найдена");
                    return;
                }
                var webClient = new WebClient();
                webClient.Headers.Add("User-Agent: Other");
                try
                {
                    Logger.AddLog($"Попытка загрузки для карточки {caseToParce.VsrfNumber}");
                    webClient.DownloadFile(pdfUrl, $"pdf\\{caseToParce.Id}.pdf");
                }
                catch (Exception ex)
                {
                    Logger.AddLog($"Загрузка для карточки {caseToParce.VsrfNumber} не удалась: {ex.Message}");
                    return;
                }
                Logger.AddLog($"Загрузка для карточки {caseToParce.VsrfNumber} успешна");
            }
            else
            {
                Logger.AddLog($"Не удалось открыть страницу для карточки {caseToParce.VsrfNumber}, проверка актуальности ссылки на карточку");
                await ParceNewLinkAsync(caseToParce, repealesContext);
            }
        }

        public static async Task<bool> ParceNewLinkAsync(Case caseToParce, RepealesContext repealesContext)
        {
            var config = Configuration.Default.WithDefaultLoader();
            var context = BrowsingContext.New(config);
            var url = $"https://vsrf.ru/lk/practice/claims?&registerDateExact=off&considerationDateExact=off&numberExact=true&number={caseToParce.VsrfNumber}";
            Logger.AddLog($"Поиск ссылки для карточки {caseToParce.VsrfNumber} на странице {url}");
            var doc = await context.OpenAsync(url);
            bool found = false;
            if (doc is not null)
            {
                var cards = doc.GetElementsByClassName("vs-items");
                foreach (var card in cards)
                {
                    var rows = card.GetElementsByClassName("row vs-border");
                    if (rows.Length == 0)
                        continue;
                    var cols = rows[0].GetElementsByClassName("col-md-7");
                    if (cols.Length == 0)
                        continue;
                    if (cols.First().TextContent.ToLower().Contains("поступило:"))
                    {
                        cols = rows.First().GetElementsByClassName("vs-items-label");
                        if (cols.Length == 0)
                            continue;
                        var a = cols.First().GetElementsByTagName("a").First();
                        var caseUrl = VsrfAddress + a.GetAttribute("href");
                        if (caseUrl != VsrfAddress && caseUrl != caseToParce.Link)
                        {
                            var caseToChange = await repealesContext.Cases.FirstAsync(c => c.Id == caseToParce.Id);
                            caseToChange.Link = caseUrl;
                            await repealesContext.SaveChangesAsync();
                        }
                    }
                }
            }
            if (found)
                Logger.AddLog($"Для карточки {caseToParce.VsrfNumber} найдена новая ссылка: {caseToParce.Link}");
            return found;
        }

        public static async Task ParceCaseAsync(string url, RepealesContext repealesContext)
        {
            var config = Configuration.Default.WithDefaultLoader();
            var context = BrowsingContext.New(config);
            var doc = await context.OpenAsync(url);
            var card = doc.GetElementsByClassName("scroll-target").First();
            var rows = card.GetElementsByClassName("row");
            var VsrfNumber = GetVsrfNumber(rows);
            Logger.AddLog($"Найдена карточка {VsrfNumber}, проверка повторности");
            var newCase = await repealesContext.Cases.FirstOrDefaultAsync(c => c.VsrfNumber == VsrfNumber);
            if (newCase is null)
            {
                string uid = GetUid(rows);
                var CaseType = GetTypeOfProduction(rows, repealesContext);
                if (CaseType is not null)
                    repealesContext.Attach(CaseType);
                var FirstStage = GetFirstStageCourt(GetVnkod(uid), repealesContext);
                if (FirstStage is not null)
                    repealesContext.Attach(FirstStage);
                newCase = new()
                {
                    Link = url,
                    VsrfNumber = VsrfNumber,
                    CaseType = CaseType,
                    VerdictDate = GetVerdictDate(rows),
                    FirstStage = FirstStage,
                    Notes = $"Загружено автоматически {DateTime.Now.ToShortDateString()}"
                };
                if (uid is not null)
                {
                    var sdpData = await SdpConnector.GetData(newCase.CaseType.Id, uid);
                    newCase.CassNumber = sdpData.CassNumber;
                    newCase.FirstStageNumber = sdpData.FirstStageNumber;
                    var Judge = repealesContext.Judges.FirstOrDefault(j => j.SdpId == sdpData.Judge);
                    if (Judge is not null)
                        repealesContext.Attach(Judge);
                    newCase.Judge = Judge;
                }
                await repealesContext.Cases.AddAsync(newCase);
                await repealesContext.SaveChangesAsync();
                Logger.AddLog($"Добавлена карточка {newCase.VsrfNumber}");
            }
            else
                Logger.AddLog($"Карточка {newCase.VsrfNumber} уже существует и не будет загружена");
        }

        private static string GetVsrfNumber(IHtmlCollection<IElement> rows)
        {
            return rows.First().GetElementsByClassName("vs-items-additional-info").First().TextContent.Trim();
        }

        private static CaseType GetTypeOfProduction(IHtmlCollection<IElement> rows, RepealesContext repealesContext)
        {
            string type = "";
            foreach (var row in rows)
            {
                var e = row.GetElementsByClassName("col-md-3");
                if (e.Length == 0)
                    continue;
                if (!e.First().TextContent.Trim().ToLower().Contains("вид судопроизводства"))
                    continue;
                type = row.GetElementsByClassName("col-md-7").First().TextContent.Trim().ToLower();
                type = type switch
                {
                    string s when s.Contains("правонаруш") => "правонаруш",
                    string s when s.Contains("уголов") => "уголовное",
                    string s when s.Contains("гражд") => "гражданское",
                    _ => "административное"
                };
                break;
            }
            return repealesContext.CaseTypes.First(t => t.Name.ToLower().Contains(type));
        }

        private static string GetUid(IHtmlCollection<IElement> rows)
        {
            string uid = "";
            foreach (var row in rows)
            {
                var e = row.GetElementsByClassName("col-md-7");
                if (e.Length == 0)
                    continue;
                var text = e.First().TextContent.Trim();
                if (Regex.Match(text, @"\d{2}\w{2}\d{4}-\d{2}-\d{4}-\d{6}-\d{2}").Success)
                {
                    uid = text;
                    break;
                }
            }
            return uid;
        }

        private static string? GetVnkod(string uid)
        {
            if (uid != "")
                return uid[..8];
            else
                return null;
        }

        private static FirstStage? GetFirstStageCourt(string? vnkod, RepealesContext repealesContext)
        {
            if (vnkod is null)
                return repealesContext.FirstStages.First(c => c.Name.Contains("несудеб"));
            else
                return repealesContext.FirstStages.FirstOrDefault(c => c.Vnkod == vnkod);
        }

        private static DateOnly GetVerdictDate(IHtmlCollection<IElement> rows)
        {
            var row = rows[^2];
            return DateOnly.Parse(row.GetElementsByClassName("col-md-2").Last().TextContent.Trim());
        }
    }
}

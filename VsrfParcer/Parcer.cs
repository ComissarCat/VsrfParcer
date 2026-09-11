using AngleSharp;
using AngleSharp.Dom;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.RegularExpressions;
using VsrfParcer.AppContext;
using VsrfParcer.Models;

namespace VsrfParcer
{
	internal class Parcer
	{
		// ВАЖНО: было "https://vsrf.ru", сайт сейчас отдаёт контент с www.
		private const string VsrfAddress = "https://www.vsrf.ru";

		private static readonly Regex UidRegex =
			new(@"\d{2}\w{2}\d{4}-\d{2}-\d{4}-\d{6}-\d{2}", RegexOptions.Compiled);

		private static readonly Regex DateRegex =
			new(@"\d{2}\.\d{2}\.\d{4}", RegexOptions.Compiled);

		/// <summary>
		/// Загружает страницу через headless-браузер (PageFetcher, см. отдельный файл)
		/// и парсит полученный HTML через AngleSharp — вся остальная логика (поиск
		/// ссылок, дат, УИД и т.п.) работает с результатом ровно так же, как раньше
		/// работала с документом, полученным напрямую по HTTP.
		/// </summary>
		private static async Task<(IDocument Doc, int Status, int BodyLength)> FetchDocumentAsync(string url)
		{
			var (html, status) = await PageFetcher.GetHtmlAsync(url);
			var context = BrowsingContext.New(Configuration.Default);
			var doc = await context.OpenAsync(req => req.Content(html).Address(url));
			return (doc, status, html.Length);
		}

		public static async Task ParceNewCasesAsync(RepealesContext repealesContext)
		{
			// Тот же смысл фильтров, что и раньше (номер оканчивается на -К4,
			// без точного совпадения номера/даты), запрос вида "новые за неделю"
			// (actNewArrival=WEEK) в текущей версии сайта убран — при ежедневном
			// запуске он не нужен: свежие дела и так первые в списке.
			string url = "https://www.vsrf.ru/lk/practice/acts?number=-%D0%9A4&numberExact=false&actDateExact=false&keywords=";

			IDocument doc;
			int status, bodyLength;
			try
			{
				(doc, status, bodyLength) = await FetchDocumentAsync(url);
			}
			catch (Exception ex)
			{
				Logger.AddLog($"Не удалось открыть страницу поиска: {ex.Message}");
				return;
			}

			Logger.AddLog($"Страница {url} открыта: статус {status}, размер тела {bodyLength} симв.");
			if (status != 200 || bodyLength < 1000)
			{
				Logger.AddLog("Похоже на блокировку/заглушку со стороны сайта — статус не 200 или тело " +
					"подозрительно маленькое даже через настоящий браузер. Это уже не должно быть связано " +
					"с обнаружением бота — стоит проверить доступность сайта в принципе (не упал ли он, " +
					"не сработала ли более широкая блокировка IP).");
			}

			// Ссылки на карточки дел ведут на /lk/practice/claims/{id},
			// а текст самой ссылки — это номер дела. Ищем по этому признаку,
			// не полагаясь на конкретный CSS-класс (он мог смениться).
			var caseLinks = doc
				.QuerySelectorAll("a[href*='/lk/practice/claims/']")
				.Where(a => Regex.IsMatch(a.TextContent.Trim(), @"-К4\s*$"))
				.ToList();

			Logger.AddLog($"Найдено ссылок на карточки дел: {caseLinks.Count}");
			if (caseLinks.Count == 0)
			{
				Logger.AddLog("Ссылок не найдено. Если статус ответа был 200 и тело не пустое — " +
					"возможно, сайт отдал не тот контент, либо снова поменялась вёрстка.");
				return;
			}

			foreach (var a in caseLinks)
			{
				var vsrfNumber = a.TextContent.Trim();
				var href = a.GetAttribute("href");
				if (string.IsNullOrWhiteSpace(href))
					continue;

				var caseUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
					? href
					: VsrfAddress + href;

				// Дата вынесения решения ищется в ближайшем родительском блоке-карточке,
				// где помимо номера дела также встречается дата в формате dd.MM.yyyy.
				var card = FindCardContainer(a);
				var dateMatch = card is null ? null : DateRegex.Match(card.TextContent);

				if (dateMatch is not { Success: true } ||
					!DateOnly.TryParseExact(dateMatch.Value, "dd.MM.yyyy",
						CultureInfo.InvariantCulture, DateTimeStyles.None, out var verdictDate))
				{
					Logger.AddLog($"Не удалось определить дату для карточки {vsrfNumber}, пропуск");
					continue;
				}

				Logger.AddLog($"Обрабатывается страница {caseUrl}");
				try
				{
					await ParceCaseAsync(caseUrl, vsrfNumber, verdictDate, repealesContext);
				}
				catch (Exception ex)
				{
					// Одна проблемная карточка (сетевой сбой, отсутствие подходящего
					// CaseType в БД и т.п.) не должна обрывать обработку остальных.
					Logger.AddLog($"Ошибка при обработке карточки {vsrfNumber}: {ex.Message}");
				}
			}
		}

		private static IElement? FindCardContainer(IElement link)
		{
			var current = link.ParentElement;
			for (var i = 0; i < 8 && current != null; i++)
			{
				if (DateRegex.IsMatch(current.TextContent))
					return current;
				current = current.ParentElement;
			}
			return link.ParentElement;
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

			IDocument doc;
			int status, bodyLength;
			try
			{
				(doc, status, bodyLength) = await FetchDocumentAsync(caseToParce.Link!);
			}
			catch (Exception ex)
			{
				Logger.AddLog($"Не удалось открыть страницу для карточки {caseToParce.VsrfNumber}: {ex.Message}, " +
					"проверка актуальности ссылки на карточку");
				await ParceNewLinkAsync(caseToParce, repealesContext);
				return;
			}

			Logger.AddLog($"Страница {caseToParce.Link} для карточки {caseToParce.VsrfNumber} открыта: " +
				$"статус {status}, размер тела {bodyLength} симв.");

			var pdfHref = FindPdfHref(doc);
			if (pdfHref is null)
			{
				// Не факт, что ссылка сломана — часто это просто значит, что документ
				// ещё не опубликован. Не трогаем Link, просто пробуем в следующий раз.
				Logger.AddLog($"Ссылка на pdf для карточки {caseToParce.VsrfNumber} пока не найдена (возможно, документ ещё не опубликован)");
				return;
			}

			var pdfUrl = pdfHref.StartsWith("http", StringComparison.OrdinalIgnoreCase)
				? pdfHref
				: VsrfAddress + pdfHref;

			Logger.AddLog($"Ссылка на pdf для карточки {caseToParce.VsrfNumber}: {pdfUrl}");

			byte[]? pdfBytes;
			try
			{
				Logger.AddLog($"Попытка загрузки для карточки {caseToParce.VsrfNumber}");
				pdfBytes = await PageFetcher.DownloadFileAsync(pdfUrl, caseToParce.Link!);
			}
			catch (Exception ex)
			{
				Logger.AddLog($"Загрузка для карточки {caseToParce.VsrfNumber} не удалась: {ex.Message}");
				return;
			}

			if (pdfBytes is null || pdfBytes.Length == 0)
			{
				Logger.AddLog($"Загрузка для карточки {caseToParce.VsrfNumber} не удалась (пустой/ошибочный ответ)");
				return;
			}

			try
			{
				Directory.CreateDirectory("pdf");
				await File.WriteAllBytesAsync(Path.Combine("pdf", $"{caseToParce.Id}.pdf"), pdfBytes);
			}
			catch (Exception ex)
			{
				Logger.AddLog($"Сохранение pdf-файла для карточки {caseToParce.VsrfNumber} не удалось: {ex.Message}");
				return;
			}

			Logger.AddLog($"Загрузка для карточки {caseToParce.VsrfNumber} успешна");
		}

		/// <summary>
		/// Ссылка на PDF ищется по href, содержащему "/lk/practice/stor_pdf/" — этот
		/// фрагмент пути не менялся (в отличие от CSS-классов). Сначала пытаемся найти
		/// её именно в блоке актуального состояния дела (там же, где "Вид
		/// судопроизводства:"), чтобы не зацепить PDF от другого движения по делу;
		/// если такой блок не найден — берём первую ссылку на PDF на странице.
		/// </summary>
		private static string? FindPdfHref(IDocument doc)
		{
			var mainBlock = FindMainCaseBlock(doc);
			var pdfLink = mainBlock?.QuerySelector("a[href*='/lk/practice/stor_pdf/']")
						  ?? doc.QuerySelector("a[href*='/lk/practice/stor_pdf/']");
			return pdfLink?.GetAttribute("href");
		}

		/// <summary>
		/// Находит блок актуального состояния дела — тот, где есть метка
		/// "Вид судопроизводства:" (она же используется в GetTypeOfProduction).
		/// </summary>
		private static IElement? FindMainCaseBlock(IDocument doc)
		{
			var labelEl = doc.All.FirstOrDefault(e =>
				e.ChildElementCount == 0 && e.TextContent.Trim() == "Вид судопроизводства:");
			if (labelEl is null)
				return null;

			var current = labelEl.ParentElement;
			for (var i = 0; i < 12 && current != null; i++)
			{
				if (current.QuerySelector("a[href*='/lk/practice/stor_pdf/']") != null)
					return current;
				current = current.ParentElement;
			}
			return labelEl.ParentElement;
		}

		/// <summary>
		/// Ищет карточку дела заново по точному номеру и, если ссылка нашлась и
		/// отличается от сохранённой, обновляет её в БД.
		/// ВАЖНО: раньше здесь был путь "/lk/practice/claims?..." — это путь СТРАНИЦЫ
		/// КОНКРЕТНОГО ДЕЛА (/lk/practice/claims/{id}), а не поиска. Сам поиск и раньше,
		/// и сейчас находится на "/lk/practice/acts". Фильтр numberExact=true вживую
		/// до конца не проверен — стоит свериться на реальном примере.
		/// </summary>
		public static async Task<bool> ParceNewLinkAsync(Case caseToParce, RepealesContext repealesContext)
		{
			var encodedNumber = Uri.EscapeDataString(caseToParce.VsrfNumber);
			var url = $"{VsrfAddress}/lk/practice/acts?number={encodedNumber}&numberExact=true&actDateExact=false&keywords=";
			Logger.AddLog($"Поиск ссылки для карточки {caseToParce.VsrfNumber} на странице {url}");

			IDocument doc;
			try
			{
				(doc, _, _) = await FetchDocumentAsync(url);
			}
			catch (Exception ex)
			{
				Logger.AddLog($"Не удалось открыть страницу поиска для карточки {caseToParce.VsrfNumber}: {ex.Message}");
				return false;
			}

			var link = doc
				.QuerySelectorAll("a[href*='/lk/practice/claims/']")
				.FirstOrDefault(a => a.TextContent.Trim() == caseToParce.VsrfNumber);

			if (link is null)
			{
				Logger.AddLog($"Для карточки {caseToParce.VsrfNumber} новая ссылка не найдена");
				return false;
			}

			var href = link.GetAttribute("href");
			if (string.IsNullOrWhiteSpace(href))
				return false;

			var caseUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : VsrfAddress + href;
			if (caseUrl == caseToParce.Link)
				return false;

			var caseToChange = await repealesContext.Cases.FirstAsync(c => c.Id == caseToParce.Id);
			caseToChange.Link = caseUrl;
			await repealesContext.SaveChangesAsync();
			caseToParce.Link = caseUrl;

			Logger.AddLog($"Для карточки {caseToParce.VsrfNumber} найдена новая ссылка: {caseUrl}");
			return true;
		}

		/// <summary>
		/// vsrfNumber и verdictDate передаются из ParceNewCasesAsync (они уже надёжно
		/// получены со страницы списка результатов) — так не нужно повторно и хрупко
		/// вычислять их на странице карточки дела.
		/// </summary>
		public static async Task ParceCaseAsync(string url, string vsrfNumber, DateOnly verdictDate, RepealesContext repealesContext)
		{
			Logger.AddLog($"Найдена карточка {vsrfNumber}, проверка повторности");
			var newCase = await repealesContext.Cases.FirstOrDefaultAsync(c => c.VsrfNumber == vsrfNumber);
			if (newCase is not null)
			{
				Logger.AddLog($"Карточка {vsrfNumber} уже существует и не будет загружена");
				return;
			}

			var (doc, status, bodyLength) = await FetchDocumentAsync(url);
			if (status != 200 || bodyLength < 500)
			{
				Logger.AddLog($"Карточка {vsrfNumber}: подозрительный ответ — статус {status}, " +
					$"размер тела {bodyLength} симв.");
			}

			var lines = GetTextLines(doc);
			string? uid = GetUid(doc);

			var caseType = GetTypeOfProduction(lines, repealesContext);
			repealesContext.Attach(caseType);
			var firstStage = GetFirstStageCourt(GetVnkod(uid), repealesContext);
			if (firstStage is not null)
				repealesContext.Attach(firstStage);

			newCase = new()
			{
				Link = url,
				VsrfNumber = vsrfNumber,
				CaseType = caseType,
				VerdictDate = verdictDate,
				FirstStage = firstStage,
				Notes = $"Загружено автоматически {DateTime.Now.ToShortDateString()}"
			};

			if (uid is not null)
			{
				try
				{
					var sdpData = await SdpConnector.GetData(newCase.CaseType.Id, uid);
					newCase.CassNumber = sdpData.CassNumber;
					newCase.FirstStageNumber = sdpData.FirstStageNumber;
					var judge = repealesContext.Judges.FirstOrDefault(j => j.SdpId == sdpData.Judge);
					if (judge is not null)
						repealesContext.Attach(judge);
					newCase.Judge = judge;
				}
				catch (Exception ex)
				{
					Logger.AddLog($"Ошибка при получении данных из СДП для карточки {vsrfNumber}: {ex.Message}");
				}
			}

			await repealesContext.Cases.AddAsync(newCase);
			await repealesContext.SaveChangesAsync();
			Logger.AddLog($"Добавлена карточка {newCase.VsrfNumber}");
		}

		/// <summary>
		/// "Строки" видимого текста страницы — по одной на каждый отдельный текстовый
		/// узел DOM (пустые/пробельные пропускаются). Не использует Split('\n') —
		/// TextContent не содержит переносов строк между элементами, если исходный
		/// HTML не отформатирован построчно.
		/// </summary>
		private static List<string> GetTextLines(IDocument doc)
		{
			var lines = new List<string>();
			if (doc.Body is not null)
				CollectTextNodes(doc.Body, lines);
			return lines;
		}

		private static void CollectTextNodes(INode node, List<string> lines)
		{
			foreach (var child in node.ChildNodes)
			{
				if (child.NodeType == AngleSharp.Dom.NodeType.Text)
				{
					var text = child.TextContent.Replace('\u00A0', ' ').Trim();
					if (text.Length > 0)
						lines.Add(text);
				}
				else if (child.NodeType == AngleSharp.Dom.NodeType.Element)
				{
					var tag = ((IElement)child).TagName;
					if (tag is "SCRIPT" or "STYLE" or "NOSCRIPT")
						continue;
					CollectTextNodes(child, lines);
				}
			}
		}

		/// <summary>Значение сразу после строки-метки (например "Вид судопроизводства:").</summary>
		private static string? GetValueAfterLabel(List<string> lines, string label)
		{
			var idx = lines.FindIndex(l => l.Equals(label, StringComparison.OrdinalIgnoreCase));
			return idx >= 0 && idx + 1 < lines.Count ? lines[idx + 1] : null;
		}

		private static CaseType GetTypeOfProduction(List<string> lines, RepealesContext repealesContext)
		{
			var value = (GetValueAfterLabel(lines, "Вид судопроизводства:") ?? "").ToLower();
			var type = value switch
			{
				string s when s.Contains("правонаруш") => "правонаруш",
				string s when s.Contains("уголов") => "уголовное",
				string s when s.Contains("гражд") => "гражданское",
				_ => "административное"
			};
			return repealesContext.CaseTypes.First(t => t.Name.ToLower().Contains(type));
		}

		/// <summary>
		/// УИД ищем regex-ом по всему HTML карточки — это устойчиво к любой
		/// перестройке вёрстки, в отличие от поиска по конкретным CSS-классам.
		/// </summary>
		private static string? GetUid(IDocument doc)
		{
			var match = UidRegex.Match(doc.DocumentElement.OuterHtml);
			return match.Success ? match.Value : null;
		}

		private static string? GetVnkod(string? uid)
		{
			return string.IsNullOrEmpty(uid) ? null : uid[..8];
		}

		private static FirstStage? GetFirstStageCourt(string? vnkod, RepealesContext repealesContext)
		{
			if (vnkod is null)
				return repealesContext.FirstStages.First(c => c.Name.Contains("несудеб"));
			else
				return repealesContext.FirstStages.FirstOrDefault(c => c.Vnkod == vnkod);
		}
	}
}

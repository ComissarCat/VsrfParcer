using VsrfParcer.AppContext;
using VsrfParcer.Models;

namespace VsrfParcer
{
    internal class Program
    {
        static async Task Main()
        {
            Logger.AddLog("Запуск");
            RepealesContext repealesContext = new();
            Logger.AddLog("Поиск новых карточек");
            await Parcer.ParceNewCasesAsync(new RepealesContext());
            Logger.AddLog("Поиск pdf");
            if (!Directory.Exists("pdf"))
                Directory.CreateDirectory("pdf");
            List<string> docs = new();
            foreach (var file in Directory.GetFiles("pdf"))
                docs.Add(file.Split('\\').Last().Split('.').First());
            List<Case> casesWithoutPdfs = repealesContext.Cases.Where(c => docs.All(d => d != c.Id.ToString())).ToList();
            foreach (var c in casesWithoutPdfs)
            {
                Logger.AddLog($"Поиск pdf для карточки {c.VsrfNumber}");
                await Parcer.ParcePdfsAsync(c, repealesContext);
            }
            Logger.DeleteOldLogs();
            Logger.AddLog("Завершение работы");
        }
    }
}

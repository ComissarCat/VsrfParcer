namespace VsrfParcer
{
    internal class Logger
    {
        public static void AddLog(string message)
        {
            string fullName = $"logs/{DateTime.Now.ToShortDateString().Replace('.', '-')}.log";
            string fullMessage = $"{DateTime.Now:hh:mm:ss} | {message}";
            if (!Directory.Exists("logs"))
                Directory.CreateDirectory("logs");
            using var streamWriter = File.AppendText(fullName);
            streamWriter.WriteLine(fullMessage);
            Console.WriteLine(fullMessage);
        }

        public static void DeleteOldLogs()
        {
            foreach (var file in Directory.GetFiles("logs"))
            {
                if ((DateTime.Now - File.GetCreationTime(file)).TotalDays > 14)
                {
                    File.Delete(file);
                    AddLog($"Удален старый лог: {file}");
                }
            }
        }
    }
}

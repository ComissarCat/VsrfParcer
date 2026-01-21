using FirebirdSql.Data.FirebirdClient;
using VsrfParcer.Models;

namespace VsrfParcer
{
    internal class SdpConnector
    {
        private static FbConnection SetConnection()
        {
            FbConnectionStringBuilder builder = new()
            {
                DataSource = "192.168.0.254",
                Port = 3050,
                Database = @"C:/DATA/JUSTICE/uni_work2003.gdb",
                UserID = "SYSDBA",
                Password = "m",
                Dialect = 1
            };
            return new(builder.ConnectionString);
        }

        public static async Task<SdpData> GetData(int productionType, string uid)
        {
            SdpData data = new();
            string commandText = productionType switch
            {
                int p when p == 1 || p == 2 => $"select complaint_full_number, case_number_i, judge_speaker_id from g33_proceeding where upper(judicial_uid) like upper('{uid}')",
                int p when p == 3 => $"select complaint_full_number, case_number_i, judge_study_id from a33_proceeding where upper(judicial_uid) like upper('{uid}')",
                int p when p == 4 => $"select complaint_full_number, case_number_i, judge_speaker_id from u33_proceeding where upper(judicial_uid) like upper('{uid}')",
            };
            var connection = SetConnection();
            try
            {
                await connection.OpenAsync();
            }
            catch
            {
                return data;
            }
            FbCommand command = new(commandText, connection);
            var reader = await command.ExecuteReaderAsync();
            if (reader.HasRows)
            {
                while (await reader.ReadAsync())
                {
                    data.CassNumber = await reader.IsDBNullAsync(0) ? null : reader.GetString(0);
                    data.FirstStageNumber = await reader.IsDBNullAsync(1) ? null : reader.GetString(1);
                    data.Judge = await reader.IsDBNullAsync(2) ? null : reader.GetInt32(2);
                    break;
                }
            }
            await reader.CloseAsync();
            await connection.CloseAsync();
            return data;
        }
    }
}

using System.ComponentModel.DataAnnotations.Schema;

namespace VsrfParcer.Models
{
    internal class Case
    {
        public int Id { get; set; }
        public DateOnly VerdictDate { get; set; }
        public string VsrfNumber { get; set; }
        public FirstStage? FirstStage { get; set; }
        public string? FirstStageNumber { get; set; }
        public string? CassNumber { get; set; }
        public CaseType? CaseType { get; set; }
        public Judge? Judge { get; set; }
        public string? Link { get; set; }
        public string? Notes { get; set; }
        //public override string ToString()
        //{
        //    string judge = Judge is null ? "" : Judge.Name;
        //    string firstStage = FirstStageCourt is null ? "" : FirstStageCourt.First_stage_court;
        //    string vnkod = FirstStageCourt is null ? "" : FirstStageCourt.VnCode;
        //    return $"Verdict_date: {Verdict_date.ToShortDateString()}\nVSRF_number: {VSRF_number}\nCassationNumber: {Cassation_number}\nJudge: {judge}\nTypeOfProduction: {TypeOfProduction.Type_of_production}\nFirstStageNumber: {First_stage_number}\nFirstStageCourt: {firstStage}\nVnkod: {vnkod}\nLink_to_VSRF_card: {Link_to_VSRF_card}\nNotes: {Notes}";
        //}
    }
}

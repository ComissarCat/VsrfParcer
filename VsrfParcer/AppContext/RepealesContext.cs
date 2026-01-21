using Microsoft.EntityFrameworkCore;
using VsrfParcer.Models;

namespace VsrfParcer.AppContext
{
    internal class RepealesContext : DbContext
    {
        public DbSet<Case> Cases { get; set; }
        public DbSet<FirstStage> FirstStages { get; set; }
        public DbSet<Judge> Judges { get; set; }
        public DbSet<CaseType> CaseTypes { get; set; }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            string connectionString = "server = 192.168.0.18; user = root; password = aN271828; database = repealesNew";
            optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
        }
    }
}

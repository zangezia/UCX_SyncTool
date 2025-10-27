using System.Collections.Generic;

namespace UCXSyncTool.Models
{
    public class AppSettings
    {
        public List<string> CachedProjects { get; set; } = new List<string>();
        public string? LastProject { get; set; }
        public string? DestRoot { get; set; }
        public int IdleMinutes { get; set; } = 5;
        public int RobocopyThreads { get; set; } = 8;
        public override string ToString() => $"LastProject={LastProject}, DestRoot={DestRoot}, IdleMinutes={IdleMinutes}, CachedCount={CachedProjects?.Count}";
    }
}

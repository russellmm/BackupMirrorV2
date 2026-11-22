namespace BackupMirror.Service.Runtime;

public sealed class ServiceOptions
{
    public string Urls { get; set; } = "http://127.0.0.1:8737";
    public int MaxConcurrentJobs { get; set; } = 2;
    public int RealtimeCatchUpMinutes { get; set; } = 5;
    public int ScheduleCatchUpHours { get; set; } = 12;

    /// <summary>Host names (besides localhost, IP literals and this machine's name) accepted in the Host header.</summary>
    public string[] ExtraAllowedHosts { get; set; } = Array.Empty<string>();

    public string DataRoot { get; set; } = "";
}

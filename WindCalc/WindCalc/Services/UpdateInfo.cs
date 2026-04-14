using System;

namespace WindCalc.Services
{
    public class UpdateInfo
    {
        public Version Version { get; set; }
        public string DownloadUrl { get; set; }
        public string ReleaseNotes { get; set; }
        public string LocalInstallerPath { get; set; }
    }
}

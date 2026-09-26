using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DirectPackageInstaller
{
    public struct Settings
    {
        public string EthernetAdapter;

        public string AllDebridApiKey;
        public string RealDebridApiKey;
        public string DebridLinkApiKey;
        public string PSIP;
        public string PCIP;

        public int? PayloadPort;

        public bool UseAllDebrid;
        public bool UseRealDebrid;
        public bool UseDebridLink;
        public bool SearchPS4;
        public bool ProxyDownload;
        public bool SegmentedDownload;
        public bool SkipUpdateCheck;

        public bool EnableDHCP;

        public bool EnableCNL;

        public bool ShowError;
        public bool ShowTransferProgress;

        /// <summary>Ask the console what is installed right after startup.</summary>
        public bool AutoCheckConsole;

        /// <summary>Look up the latest official update of each title (Sony's public patch server).</summary>
        public bool CheckOfficialUpdates;

        /// <summary>Use Payload/payload_experimental.bin for GoldHEN installs (free space, storage choice).</summary>
        public bool ExperimentalPayload;

        /// <summary>Experimental payload only: -1 console setting, 0 system storage, 1 extended storage.</summary>
        public int InstallStorage;

        public bool AutoSplitPKG;

        /// <summary>Where save backups go ("" = Documents\DPI Save Backups).</summary>
        public string? SaveBackupFolder;
    }
}

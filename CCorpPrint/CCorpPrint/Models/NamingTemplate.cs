using System.Runtime.Serialization;

namespace CCorpPrint.Models
{
    [DataContract]
    public class NamingTemplate
    {
        [DataMember] public string Name { get; set; } = "";
        [DataMember] public string PerSheet { get; set; } = "{Sheet Number}_{Sheet Name}";
        [DataMember] public string Combined { get; set; } = "{ProjectFileName}_FullSet_{Today:yyyy-MM-dd}";
        [DataMember] public string FolderTemplate { get; set; } = "";
    }
}

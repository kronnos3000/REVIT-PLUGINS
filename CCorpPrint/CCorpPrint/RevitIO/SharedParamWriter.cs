using System.IO;
using Autodesk.Revit.DB;

namespace CCorpPrint.RevitIO
{
    /// <summary>
    /// Defines and binds the single CCorpPrint shared parameter we use:
    /// CCORP_PRINT_TEMPLATES_JSON (Text, Identity Data, ProjectInformation).
    /// Stores per-project naming templates so they travel with the .rvt.
    /// </summary>
    public class SharedParamWriter
    {
        public const string ParamName = "CCORP_PRINT_TEMPLATES_JSON";
        private const string GroupName = "CCorp Print";

        private static readonly ForgeTypeId TextSpec =
            new ForgeTypeId("autodesk.spec:spec.string-2.0.0");

        private readonly Document _doc;

        public SharedParamWriter(Document doc) { _doc = doc; }

        /// <summary>
        /// Returns the bound Parameter on ProjectInformation, creating the shared
        /// param definition + binding if needed. Must be called inside an open Transaction.
        /// </summary>
        public Parameter EnsureParameter()
        {
            var existing = _doc.ProjectInformation.LookupParameter(ParamName);
            if (existing != null) return existing;

            EnsureSharedParameterFile();

            var defFile = _doc.Application.OpenSharedParameterFile();
            if (defFile == null) return null;

            var group = defFile.Groups.get_Item(GroupName) ?? defFile.Groups.Create(GroupName);

            ExternalDefinition extDef;
            var existingDef = group.Definitions.get_Item(ParamName);
            if (existingDef is ExternalDefinition ed)
            {
                extDef = ed;
            }
            else
            {
                var opts = new ExternalDefinitionCreationOptions(ParamName, TextSpec)
                {
                    Visible = true,
                    UserModifiable = true,
                    Description = "JSON-encoded CCorpPrint naming templates for this project."
                };
                extDef = group.Definitions.Create(opts) as ExternalDefinition;
            }

            if (extDef == null) return null;

            var projInfoCat = _doc.Settings.Categories.get_Item(BuiltInCategory.OST_ProjectInformation);
            var catSet = new CategorySet();
            catSet.Insert(projInfoCat);

            Binding binding = new InstanceBinding(catSet);
            _doc.ParameterBindings.Insert(extDef, binding);

            return _doc.ProjectInformation.LookupParameter(ParamName);
        }

        private void EnsureSharedParameterFile()
        {
            var current = _doc.Application.SharedParametersFilename;
            if (!string.IsNullOrEmpty(current) && File.Exists(current)) return;

            var path = Path.Combine(Path.GetTempPath(), "CCorpPrint_SharedParams.txt");
            if (!File.Exists(path))
                File.WriteAllText(path, "# CCorpPrint Shared Parameters\n");
            _doc.Application.SharedParametersFilename = path;
        }
    }
}

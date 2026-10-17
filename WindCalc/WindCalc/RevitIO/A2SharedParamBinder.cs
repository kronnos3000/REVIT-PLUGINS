using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace WindCalc.RevitIO
{
    /// <summary>
    /// Creates and binds A2 shared parameters to the Revit document.
    /// Handles both ProjectInformation (instance) and Windows/Doors (type + instance) bindings.
    /// </summary>
    public class A2SharedParamBinder
    {
        private readonly Document _doc;
        private readonly string _sharedParamFilePath;

        public A2SharedParamBinder(Document doc, string sharedParamFilePath = null)
        {
            _doc = doc;
            _sharedParamFilePath = sharedParamFilePath;
        }

        /// <summary>
        /// Ensures all A2 shared parameters exist and are bound to appropriate categories.
        /// Call this before attempting to read/write A2 parameters.
        /// </summary>
        public void EnsureAllParameters()
        {
            EnsureSharedParamFile();

            using var tx = new Transaction(_doc, "Bind A2 Shared Parameters");
            tx.Start();

            try
            {
                foreach (var paramDef in A2SharedParams.GetAllParams())
                {
                    EnsureParameter(paramDef);
                }
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }
        }

        /// <summary>
        /// Ensures a specific subset of parameters are bound.
        /// </summary>
        public void EnsureParameters(IEnumerable<A2SharedParams.ParamDef> paramDefs)
        {
            EnsureSharedParamFile();

            using var tx = new Transaction(_doc, "Bind A2 Shared Parameters");
            tx.Start();

            try
            {
                foreach (var paramDef in paramDefs)
                {
                    EnsureParameter(paramDef);
                }
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }
        }

        private void EnsureSharedParamFile()
        {
            // Use configured path or fallback to temp file
            string spFile = string.IsNullOrWhiteSpace(_sharedParamFilePath)
                ? Path.Combine(Path.GetTempPath(), "WindCalc_A2_SharedParams.txt")
                : _sharedParamFilePath;

            if (!File.Exists(spFile))
            {
                File.WriteAllText(spFile, "# Wind Calculator A2 Shared Parameters\n");
            }

            _doc.Application.SharedParametersFilename = spFile;
        }

        private void EnsureParameter(A2SharedParams.ParamDef paramDef)
        {
            // Build category set for this parameter
            var catSet = new CategorySet();
            foreach (var catId in paramDef.Categories)
            {
                var cat = _doc.Settings.Categories.get_Item(catId);
                if (cat != null)
                    catSet.Insert(cat);
            }

            if (catSet.Size == 0) return;

            // Check if already bound
            if (IsParameterBound(paramDef.Name, paramDef.Categories))
                return;

            // Get or create external definition
            var extDef = GetOrCreateExternalDefinition(paramDef);
            if (extDef == null) return;

            // Create appropriate binding
            Binding binding = paramDef.IsInstance
                ? (Binding)new InstanceBinding(catSet)
                : new TypeBinding(catSet);

            _doc.ParameterBindings.Insert(extDef, binding);
        }

        private bool IsParameterBound(string paramName, BuiltInCategory[] categories)
        {
            // For ProjectInformation, check if param exists on project info
            if (categories.Contains(BuiltInCategory.OST_ProjectInformation))
            {
                var projInfo = _doc.ProjectInformation;
                if (projInfo?.LookupParameter(paramName) != null)
                    return true;
            }

            // For element categories, check an arbitrary element of that type
            // (More reliable: check the ParameterBindings map)
            var iter = _doc.ParameterBindings.ForwardIterator();
            while (iter.MoveNext())
            {
                var def = iter.Key as Definition;
                if (def?.Name == paramName)
                    return true;
            }

            return false;
        }

        private ExternalDefinition GetOrCreateExternalDefinition(A2SharedParams.ParamDef paramDef)
        {
            try
            {
                var defFile = _doc.Application.OpenSharedParameterFile();
                if (defFile == null) return null;

                var group = defFile.Groups.get_Item(paramDef.Group)
                         ?? defFile.Groups.Create(paramDef.Group);

                // Check if definition already exists
                var existingDef = group.Definitions.get_Item(paramDef.Name);
                if (existingDef is ExternalDefinition extDef)
                    return extDef;

                // Create new definition
                var opts = new ExternalDefinitionCreationOptions(paramDef.Name, paramDef.Type)
                {
                    Visible = true,
                    UserModifiable = true,
                    Description = paramDef.Description ?? ""
                };

                // Use explicit GUID if provided
                if (paramDef.Guid.HasValue)
                    opts.GUID = paramDef.Guid.Value;

                return group.Definitions.Create(opts) as ExternalDefinition;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets a parameter from an element by name, checking both instance and type parameters.
        /// </summary>
        public static Parameter GetParameter(Element element, string paramName)
        {
            // Try instance parameter first
            var param = element.LookupParameter(paramName);
            if (param != null) return param;

            // Try type parameter
            var elementType = element.Document.GetElement(element.GetTypeId());
            return elementType?.LookupParameter(paramName);
        }

        /// <summary>
        /// Sets a parameter value, handling type conversion.
        /// </summary>
        public static bool SetParameterValue(Parameter param, object value)
        {
            if (param == null || param.IsReadOnly) return false;

            try
            {
                switch (param.StorageType)
                {
                    case StorageType.Double:
                        if (value is double d)
                            param.Set(d);
                        else if (value is int i)
                            param.Set((double)i);
                        else if (double.TryParse(value?.ToString(), out double parsed))
                            param.Set(parsed);
                        break;

                    case StorageType.Integer:
                        if (value is int intVal)
                            param.Set(intVal);
                        else if (value is bool boolVal)
                            param.Set(boolVal ? 1 : 0);
                        else if (int.TryParse(value?.ToString(), out int parsedInt))
                            param.Set(parsedInt);
                        break;

                    case StorageType.String:
                        param.Set(value?.ToString() ?? "");
                        break;

                    case StorageType.ElementId:
                        if (value is ElementId eid)
                            param.Set(eid);
                        break;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a parameter's value as the specified type.
        /// </summary>
        public static T GetParameterValue<T>(Parameter param, T defaultValue = default)
        {
            if (param == null) return defaultValue;

            try
            {
                object value = param.StorageType switch
                {
                    StorageType.Double => param.AsDouble(),
                    StorageType.Integer => param.AsInteger(),
                    StorageType.String => param.AsString(),
                    StorageType.ElementId => param.AsElementId(),
                    _ => null
                };

                if (value == null) return defaultValue;

                // Handle type conversion
                if (typeof(T) == typeof(bool) && value is int intVal)
                    return (T)(object)(intVal != 0);

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}

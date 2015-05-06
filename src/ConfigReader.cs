using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Configuration;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Configuration;
using System.Web.Hosting;
using System.Xml;
using System.Xml.XPath;
using Sitecore;
using Sitecore.Configuration;
using Sitecore.Diagnostics;
using Sitecore.Xml.Patch;

[assembly: WebActivatorEx.PreApplicationStartMethod(typeof(Efocus.Sitecore.ConditionalConfig.ConfigReader), "PreStart")]
namespace Efocus.Sitecore.ConditionalConfig
{

    public class ConfigReader : global::Sitecore.Configuration.ConfigReader, IConfigurationSectionHandler
    {
        //we need to have a _section variable, because sitecore's log4net.Appender.ConfigReader access it through reflection :s
        private XmlNode _section;
        private static readonly FieldInfo sectionField = typeof(global::Sitecore.Configuration.ConfigReader).GetField("_section", BindingFlags.NonPublic | BindingFlags.Instance);

        private static Dictionary<string, string> conditions
        {
            get
            {
                return new Dictionary<string, string>()
                    {
                        {"machineName", Environment.MachineName},
                        {"rootPath", ApplicationRootDirectrory}
                    };
            }
        }


        public static String ConditionalConfigFolderName = "Include-conditional/";
        public static String ApplicationDataFolderName = "..\\Data";
        public static List<Tuple<String, String>> CustomConfigurationVariables = new List<Tuple<string, string>>();
        public static List<String> ConfigurationIncludeDirectories = new List<string>();

        //hide "normal" .config files if they don't match
        public static void PreStart()
        {
            //first we'll try to load exclude files
            var excludefolder = Path.Combine(ConfigurationRootDirectrory, "exclude");
            var excludes = new List<string>();
            if (Directory.Exists(excludefolder))
            {
                var excludefiles = Directory.GetFiles(excludefolder, "*.config", SearchOption.AllDirectories);
                foreach (string str in excludefiles)
                {
                    //now hide conditionalconfigs
                    var hasCondition = false;
                    using (var stream = File.OpenRead(str))
                    {
                        var includeFile = new XPathDocument(stream);
                        var navigator = includeFile.CreateNavigator();
                        navigator.MoveToChild(XPathNodeType.Element);
                        if (!navigator.HasAttributes)
                            continue;
                        navigator.MoveToFirstAttribute();
                        var isMatch = true;
                        do
                        {
                            isMatch = IsElementMatch(navigator, str, ref hasCondition);
                        } while (navigator.MoveToNextAttribute());
                        if (isMatch)
                        {
                            navigator.MoveToParent();
                            navigator.MoveToChild(XPathNodeType.Element);
                            do
                            {
                                if (navigator.HasAttributes)
                                {
                                    navigator.MoveToFirstAttribute();
                                    excludes.Add(navigator.Value.ToLowerInvariant());
                                    navigator.MoveToParent();
                                }
                            } while (navigator.MoveToNext(XPathNodeType.Element));
                        }
                    }

                }
            }
            var folder = Path.Combine(ConfigurationRootDirectrory, "include");
            if (!Directory.Exists(folder))
                return;
            var files = Directory.GetFiles(folder, "*.config", SearchOption.AllDirectories);
            Array.Sort(files);
            foreach (string str in files)
            {
                try
                {
                    //update packages create config files like "myconfig.config.d50f2217-de81-4265-b365-95a4c72c928b" when it doesn't want to overwrite an existing file, let's overwrite it now
                    var newConfigFiles = Directory.GetFiles(Path.GetDirectoryName(str), Path.GetFileName(str + ".*"))
                        .Where(f => f != str && IsNonReplacedConfig(f));
                    if (newConfigFiles.Any())
                    {
                        var newestConfigFile = newConfigFiles.OrderByDescending(File.GetLastWriteTimeUtc).First();
                        File.Replace(newestConfigFile, str, null);
                        foreach (string deleteme in newConfigFiles)
                            File.Delete(deleteme);
                    }

                    //now hide conditionalconfigs
                    var isMatch = true;
                    if (excludes.Contains(str.Replace(folder + "\\", "").ToLowerInvariant()))
                    {
                        isMatch = false;
                    }
                    else
                    {
                        //fastest scan for condition- on rootnode (should be in the first 5 lines):
                        if (!File.ReadLines(str).Take(5).Any(l => l.Contains("condition-")))
                        {
                            continue;
                        }
                        using (var stream = File.OpenRead(str))
                        {
                            var includeFile = new XPathDocument(stream);
                            var navigator = includeFile.CreateNavigator();
                            navigator.MoveToChild(XPathNodeType.Element);
                            if (!navigator.HasAttributes)
                                continue;
                            navigator.MoveToFirstAttribute();
                            do
                            {
                                bool hasCondition = false;
                                isMatch = IsElementMatch(navigator, str, ref hasCondition);
                            } while (navigator.MoveToNextAttribute());
                        }
                    }
                    var attributes = File.GetAttributes(str);
                    File.SetAttributes(str, isMatch ? attributes & ~FileAttributes.Hidden : attributes | FileAttributes.Hidden);
                }
                catch (Exception ex)
                {
                    Trace.TraceError(string.Concat(new object[4]
                    {
                        (object) "Could not load configuration file: ",
                        (object) str,
                        (object) ": ",
                        (object) ex
                    }));
                }
            }

        }

        private static bool IsElementMatch(XPathNavigator navigator, string str, ref bool hasCondition)
        {
            var isMatch = false;
            if (navigator.Name.StartsWith("condition-"))
            {
                hasCondition = true;
                var isNotOperator = navigator.Name.StartsWith("condition-not-");
                var valueName = navigator.Name.Substring(isNotOperator ? "condition-not-".Length : "condition-".Length);
                if (conditions.ContainsKey(valueName))
                {
                    var value = conditions[valueName];
                    var regex = new Regex(navigator.Value);
                    isMatch = regex.IsMatch(value);
                    if (!isMatch)
                    {
                        Trace.TraceInformation("Condition '{0}=\"{2}\"' is no match with '{1}', skipping file '{3}'", valueName,
                            value, navigator.Value, str);
                        return isMatch;
                    }
                }
                else
                {
                    Trace.TraceInformation("Condition '{0}' is not a valid condition (unknown). Skipping file '{1}'", valueName,
                        str);
                    return isMatch;
                }
            }
            return isMatch;
        }

        /// <summary>
        /// The root directory for the application
        /// </summary>
        public static String ApplicationRootDirectrory
        {
            get
            {
                var enviourmentHostPath = HostingEnvironment.MapPath("/");
                if (!String.IsNullOrEmpty(enviourmentHostPath))
                    return enviourmentHostPath;

                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        /// <summary>
        /// The root directory for the application
        /// </summary>
        public static String ConfigurationRootDirectrory
        {
            get
            {
                var enviourmentHostPath = HostingEnvironment.MapPath("/App_Config");
                if (!String.IsNullOrEmpty(enviourmentHostPath))
                    return enviourmentHostPath;

                var configurationFile = new FileInfo(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);
                if (!String.IsNullOrEmpty(configurationFile.DirectoryName))
                    return Path.Combine(configurationFile.DirectoryName, "App_Config");
                else
                    return Path.Combine(ApplicationRootDirectrory, "App_Config");
            }
        }

        object IConfigurationSectionHandler.Create(object parent, object configContext, XmlNode section)
        {
            AddVariables(section);

            //Expand auto include-Condional
            LoadAutoIncludeFiles(section, Path.Combine(ConfigurationRootDirectrory, ConditionalConfigFolderName));

            AddCustomIncludeDirectories(section);
            //var configFile = string.Format()"OTAP/Web/{0}.config", otapNode.Attribute("inherit").Value;

            sectionField.SetValue(this, section);
            _section = section;

            return this;
        }

        /// <summary>
        /// Add the given directories
        /// </summary>
        /// <param name="section"></param>
        private void AddCustomIncludeDirectories(XmlNode section)
        {
            foreach (var includeDirectory in ConfigurationIncludeDirectories)
            {
                if (Directory.Exists(includeDirectory))
                {
                    AddCustomIncludeDirectory(section, includeDirectory);
                }
            }
        }

        /// <summary>
        /// Add from a given directories the config files
        /// </summary>
        /// <param name="section"></param>
        /// <param name="includeDirectory"></param>
        private void AddCustomIncludeDirectory(XmlNode section, string includeDirectory)
        {
            //In order to function propperly the files must be first and secondly the directories
            var files = Directory.GetFiles(includeDirectory, "*.config");
            Array.Sort(files);
            foreach (var file in files)
            {
                ApplyPatch(file, section);
            }
            var directories = Directory.GetDirectories(includeDirectory);

            Array.Sort(directories);
            foreach (var childDirectory in directories)
            {
                AddCustomIncludeDirectory(section, childDirectory);
            }
        }

        private void AddVariables(XmlNode section)
        {
            if (section == null || section.OwnerDocument == null) return;

            foreach (var customItem in CustomConfigurationVariables)
            {
                var customVariableElement = section.OwnerDocument.CreateElement("sc.variable");
                customVariableElement.SetAttribute("name", customItem.Item1);
                customVariableElement.SetAttribute("value", customItem.Item2);
                section.InsertBefore(customVariableElement, section.FirstChild);
            }

            var dataFolderNode = section.OwnerDocument.DocumentElement.SelectSingleNode("sc.variable[@name='dataFolder']");
            if (dataFolderNode == null)
            {
                //set default datafolder (convention over configuration)
                var dataFolderElement = section.OwnerDocument.CreateElement("sc.variable");
                dataFolderElement.SetAttribute("name", "dataFolder");
                dataFolderElement.SetAttribute("value", Path.GetFullPath(Path.Combine(ApplicationRootDirectrory, ApplicationDataFolderName)));
                section.InsertBefore(dataFolderElement, section.FirstChild);
            }
            else if (dataFolderNode.Attributes["value"].Value == "/data")
            {
                dataFolderNode.Attributes["value"].Value = Path.GetFullPath(Path.Combine(ApplicationRootDirectrory, ApplicationDataFolderName));
            }
        }

        private void LoadAutoIncludeFiles(XmlNode rootNode, string folder)
        {
            Assert.ArgumentNotNull((object)rootNode, "rootNode");
            Assert.ArgumentNotNull((object)folder, "folder");

            try
            {
                if (!Directory.Exists(folder))
                    return;
                var files = Directory.GetFiles(folder, "*.config", SearchOption.AllDirectories);
                Array.Sort(files);
                foreach (string str in files)
                {
                    try
                    {
                        //update packages create config files like "myconfig.config.e2e27694-282b-459f-b6d5-eebb0e93cfc4" when it doesn't want to overwrite an existing file, let's overwrite it now
                        Guid temp;
                        var newConfigFiles = Directory.GetFiles(Path.GetDirectoryName(str), Path.GetFileName(str + ".*"))
                            .Where(f => f != str && IsNonReplacedConfig(f));
                        if (newConfigFiles.Any())
                        {
                            var newestConfigFile = newConfigFiles.OrderByDescending(File.GetLastWriteTimeUtc).First();
                            File.Replace(newestConfigFile, str, null);
                            foreach (string deleteme in newConfigFiles)
                                File.Delete(deleteme);
                        }

                        if ((File.GetAttributes(str) & FileAttributes.Hidden) == (FileAttributes)0)
                        {
                            var includeFile = new XmlDocument();
                            includeFile.XmlResolver = null;//ComSEC audit: prevent DTD injection
                            includeFile.Load(str);
                            var isMatch = true;
                            foreach (XmlAttribute attribute in includeFile.DocumentElement.Attributes)
                            {
                                if (attribute.Name.StartsWith("condition-"))
                                {
                                    var isNotOperator = attribute.Name.StartsWith("condition-not-");
                                    var valueName = attribute.Name.Substring(isNotOperator ? "condition-not-".Length : "condition-".Length);
                                    if (conditions.ContainsKey(valueName))
                                    {
                                        var value = conditions[valueName];
                                        var regex = new Regex(attribute.Value);
                                        isMatch = regex.IsMatch(value);
                                        if (!isMatch)
                                        {
                                            Log.Info(String.Format("Condition '{0}=\"{2}\"' is no match with '{1}', skipping file '{3}'", valueName, value, attribute.Value, str), this);
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        isMatch = false;
                                        Log.Info(String.Format("Condition '{0}' is not a valid condition (unknown). Skipping file '{1}'", valueName, str), this);
                                        break;
                                    }
                                }
                            }
                            if (isMatch)
                            {
                                if ("connectionStrings".Equals(includeFile.DocumentElement.Name, StringComparison.InvariantCultureIgnoreCase))
                                {
                                    Log.Info(String.Format("Found connection string file '{0}', going to patch", str), this);
                                    PatchConnectionStrings(includeFile.DocumentElement);
                                }
                                else if ("mailSettings".Equals(includeFile.DocumentElement.Name, StringComparison.InvariantCultureIgnoreCase))
                                {
                                    Log.Info(String.Format("Found mail settings file '{0}', going to patch", str), this);
                                    PatchMailSettings(includeFile.DocumentElement);
                                }
                                else
                                {
                                    Log.Info(String.Format("Including file '{0}'", str), this);
                                    //var includeElement = rootNode.OwnerDocument.CreateElement("sc.include");
                                    //includeElement.SetAttribute("file", str);
                                    //rootNode.AppendChild(includeElement);
                                    ApplyPatch(str, rootNode);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(string.Concat(new object[4]
                            {
                                (object) "Could not load configuration file: ",
                                (object) str,
                                (object) ": ",
                                (object) ex
                            }), typeof(ConfigReader));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(string.Concat(new object[4]
                    {
                        (object) "Could not scan configuration folder ",
                        (object) folder,
                        (object) " for files: ",
                        (object) ex
                    }), typeof(ConfigReader));
            }
        }

        private static bool IsNonReplacedConfig(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            var extension = Path.GetExtension(fileName).Trim('.');
            if ("config".Equals(extension, StringComparison.InvariantCultureIgnoreCase))
                return false;
            Guid temp;
            if (Guid.TryParse(extension, out temp))
                return true;
            if (extension.Contains("-")) //could be that the filename is "files-guid.config"
                extension = extension.Substring(extension.IndexOf('-')+1);
            return Guid.TryParse(extension, out temp);
        }

        private XmlPatcher _patcher = new XmlPatcher("http://www.sitecore.net/xmlconfig/set/", "http://www.sitecore.net/xmlconfig/");
        public void ApplyPatch(TextReader patch, XmlNode root)
        {
            var reader = new XmlTextReader(patch)
            {
                WhitespaceHandling = WhitespaceHandling.None
            };
            reader.MoveToContent();
            reader.ReadStartElement("configuration");
            if (reader.NodeType != XmlNodeType.EndElement)
            {
                this._patcher.Merge(root, new XmlReaderSource(reader));
                reader.ReadEndElement();
            }
        }

        public void ApplyPatch(string filename, XmlNode root)
        {
            using (var reader = new StreamReader(filename, Encoding.UTF8))
            {
                this.ApplyPatch(reader, root);
            }
        }

        private static void PatchConnectionStrings(XmlNode rootElement)
        {
            if (rootElement == null) return;

            foreach (XmlNode connectionstring in rootElement.ChildNodes)
            {
                if (connectionstring.Attributes != null && connectionstring.Attributes["value"] != null && connectionstring.Attributes["name"] != null)
                {
                    var providerName = connectionstring.Attributes["providerName"] != null
                                           ? connectionstring.Attributes["providerName"].Value
                                           : string.Empty;
                    ReplaceConnectionString(connectionstring.Attributes["name"].Value, connectionstring.Attributes["value"].Value, providerName);
                }
            }
        }

        private static void ReplaceConnectionString(string name, string value, string providerName)
        {
            //depends on microsoft not changing this code so throw exception when it does
            var settingReadyOnlyField = typeof(ConfigurationElement).GetField("_bReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
            if (settingReadyOnlyField == null) throw new Exception("Unable to replace Connectionstrings!");

            //depends on microsoft not changing this code so throw exception when it does
            var collection = ConfigurationManager.ConnectionStrings;
            var collectionReadOnlyField = typeof(ConfigurationElementCollection).GetField("bReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
            if (collectionReadOnlyField == null) throw new Exception("Unable to add Connectionstrings!");
            collectionReadOnlyField.SetValue(collection, false);

            var setting = ConfigurationManager.ConnectionStrings[name];
            if (setting == null)
            {
                ConfigurationManager.ConnectionStrings.Add(string.IsNullOrEmpty(providerName)
                                                               ? new ConnectionStringSettings(name, value)
                                                               : new ConnectionStringSettings(name, value, providerName));
            }
            else
            {
                //set readonly property to false and update connectionstring
                settingReadyOnlyField.SetValue(setting, false);
                setting.ConnectionString = value;
                if (!string.IsNullOrEmpty(providerName))
                    setting.ProviderName = providerName;
            }
        }

        private static void PatchMailSettings(XmlNode rootElement)
        {
            if (rootElement == null || rootElement.FirstChild == null || rootElement.FirstChild.Name != "smtp") return;
            //depends on microsoft not changing this code so throw exception when it does
            var settingReadyOnlyField = typeof(ConfigurationElement).GetField("_bReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
            if (settingReadyOnlyField == null) throw new Exception("Unable to replace mail settings!");

            //depends on microsoft not changing this code so throw exception when it does
            var collection = ConfigurationManager.ConnectionStrings;
            var collectionReadOnlyField = typeof(ConfigurationElementCollection).GetField("bReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
            if (collectionReadOnlyField == null) throw new Exception("Unable to add mail settings!");
            collectionReadOnlyField.SetValue(collection, false);

            var setting = (SmtpSection)ConfigurationManager.GetSection("system.net/mailSettings/smtp");

            settingReadyOnlyField.SetValue(setting, false);

            var element = rootElement.FirstChild.Clone();
            var deserializeMethod = typeof(ConfigurationSection).GetMethod("DeserializeSection", BindingFlags.Instance | BindingFlags.NonPublic);
            deserializeMethod.Invoke(setting, new object[] { new XmlNodeReader(element) });
            settingReadyOnlyField.SetValue(setting.SpecifiedPickupDirectory, false);

            var pickupDirectoryLocation = setting.SpecifiedPickupDirectory.PickupDirectoryLocation;
            if (!string.IsNullOrEmpty(pickupDirectoryLocation) && !Path.IsPathRooted(pickupDirectoryLocation))
                pickupDirectoryLocation = Path.Combine(HttpRuntime.AppDomainAppPath, pickupDirectoryLocation);
            setting.SpecifiedPickupDirectory.PickupDirectoryLocation = pickupDirectoryLocation;

            ConfigurationManager.RefreshSection("system.net/mailSettings/smtp");

            if (setting.DeliveryMethod.ToString().Equals("SpecifiedPickupDirectory", StringComparison.InvariantCultureIgnoreCase) && !Directory.Exists(setting.SpecifiedPickupDirectory.PickupDirectoryLocation))
                Directory.CreateDirectory(setting.SpecifiedPickupDirectory.PickupDirectoryLocation);
        }
    }
}
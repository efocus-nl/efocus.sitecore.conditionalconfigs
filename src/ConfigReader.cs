using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Configuration;
using System.Reflection;
using System.Web;
using System.Web.Hosting;
using System.Xml;
using Sitecore;
using Sitecore.Diagnostics;

[assembly: WebActivatorEx.PreApplicationStartMethod(typeof(Efocus.Sitecore.ConditionalConfig.ConfigReader), "PreStart")]
namespace Efocus.Sitecore.ConditionalConfig
{
    public class ConfigReader : global::Sitecore.Configuration.ConfigReader
    {
        private static readonly object excludeLoadLock = new object();
        private List<string> _excludes;
        public static string ConditionalConfigFolderName = "Include-conditional/";
        public static string ConnectionStringsFolderName = "Include-conditional/ConnectionStrings";
        public static string MailsettingsFolderName = "Include-conditional/MailSettings";
        public static string ApplicationDataFolderName = "..\\Data";
        public static List<Tuple<string, string>> CustomConfigurationVariables = new List<Tuple<string, string>>();
        public static List<string> ConfigurationIncludeDirectories = new List<string>();

        /// <summary>
        /// update packages create config files like "myconfig.config.d50f2217-de81-4265-b365-95a4c72c928b" when it doesn't want to overwrite an existing file, let's overwrite it now
        /// and patch connectionstrings and smtp settings
        /// </summary>
        public static void PreStart()
        {
            var folder = Path.Combine(ConfigurationRootDirectory, "include");
            if (Directory.Exists(folder))
            {
                //update packages create config files like "myconfig.config.d50f2217-de81-4265-b365-95a4c72c928b" when it doesn't want to overwrite an existing file, let's overwrite it now
                var files = Directory.GetFiles(folder, "*.config", SearchOption.AllDirectories);
                Array.Sort(files);
                foreach (var file in files)
                {
                    var newConfigFiles = Directory.GetFiles(Path.GetDirectoryName(file),
                            Path.GetFileName(file + ".*"))
                        .Where(f => f != file && IsNonReplacedConfig(f));
                    if (newConfigFiles.Any())
                    {
                        var newestConfigFile = newConfigFiles.OrderByDescending(File.GetLastWriteTimeUtc).First();
                        File.Replace(newestConfigFile, file, null);
                        foreach (string deleteme in newConfigFiles)
                            File.Delete(deleteme);
                    }
                }
            }
            //connectionstrings
            folder = Path.Combine(ConfigurationRootDirectory, ConnectionStringsFolderName);
            if (Directory.Exists(folder))
            {
                var files = Directory.GetFiles(folder, "*.config", SearchOption.AllDirectories);
                Array.Sort(files);
                foreach (var file in files)
                {
                    if ((File.GetAttributes(file) & FileAttributes.Hidden) == (FileAttributes) 0)
                    {
                        var includeFile = new XmlDocument();
                        includeFile.XmlResolver = null; //ComSEC audit: prevent DTD injection
                        includeFile.Load(file);
                        if (ConfigPatcher.IsElementMatch(includeFile.DocumentElement, file))
                        {
                            Trace.TraceInformation("Found connection string file '{0}', going to patch", file);
                            PatchConnectionStrings(includeFile.DocumentElement);
                        }
                    }
                }
            }
            //mailsettings
            folder = Path.Combine(ConfigurationRootDirectory, MailsettingsFolderName);
            if (Directory.Exists(folder))
            {
                var files = Directory.GetFiles(folder, "*.config", SearchOption.AllDirectories);
                Array.Sort(files);
                foreach (var file in files)
                {
                    if ((File.GetAttributes(file) & FileAttributes.Hidden) == (FileAttributes) 0)
                    {
                        var includeFile = new XmlDocument();
                        includeFile.XmlResolver = null; //ComSEC audit: prevent DTD injection
                        includeFile.Load(file);
                        if (ConfigPatcher.IsElementMatch(includeFile.DocumentElement, file))
                        {
                            Trace.TraceInformation("Found mail settings file '{0}', going to patch", file);
                            PatchMailSettings(includeFile.DocumentElement);
                        }
                    }
                }
            }
        }

        protected override global::Sitecore.Configuration.ConfigPatcher GetConfigPatcher(XmlNode element)
        {
            return new ConfigPatcher(element);
        }

        protected override void ExpandIncludeFiles(XmlNode rootNode, Hashtable cycleDetector)
        {
            if (typeof(global::Sitecore.Configuration.ConfigPatcher).Assembly.GetName().Version.Major < 10)
            {
                throw new Exception("Conditionalconfig version 3.0 or higher is is for sitecore 8.2 or higher only. Please use a 2.x version of conditionalconfig for previous sitecore versions.");
            }
            //first one called in base GetConfiguration
            LoadExcludes(rootNode);
            AddVariables(rootNode);
            base.ExpandIncludeFiles(rootNode, cycleDetector);
        }

        private void LoadExcludes(XmlNode node)
        {
            if (_excludes != null)
            {
                return;
            }
            lock (excludeLoadLock)
            {
                if (_excludes != null)
                    return;

                //first we'll try to load exclude files
                var excludefolder = Path.Combine(ConfigurationRootDirectory, "exclude");
                _excludes = new List<string>();
                if (Directory.Exists(excludefolder))
                {
                    var excludefiles = Directory.GetFiles(excludefolder, "*.config", SearchOption.AllDirectories);
                    foreach (string str in excludefiles)
                    {
                        //now hide conditionalconfigs
                        using (var stream = File.OpenRead(str))
                        using (var xmlTextReader = new XmlTextReader(stream))
                        {
                            var xmlDocument = new XmlDocument();
                            xmlDocument.Load(xmlTextReader);
                            if (ConfigPatcher.IsElementMatch(xmlDocument.DocumentElement, str))
                            {
                                foreach (XmlNode child in xmlDocument.DocumentElement.ChildNodes)
                                {
                                    var attr = child.Attributes.GetNamedItem("value");
                                    if (attr != null)
                                        _excludes.Add(attr.Value.ToLowerInvariant().TrimStart('\\','/'));
                                }
                            }
                        }

                    }
                }

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


        protected override void LoadAutoIncludeFiles(XmlNode element)
        {
            Assert.ArgumentNotNull((object)element, "element");
            var configPatcher = this.GetConfigPatcher(element);
            LoadAutoIncludeFiles(configPatcher, Path.Combine(ConfigurationRootDirectory, ConditionalConfigFolderName));
            this.LoadAutoIncludeFiles(configPatcher, MainUtil.MapPath("/App_Config/Sitecore/Components"));
            this.LoadAutoIncludeFiles(configPatcher, MainUtil.MapPath("/App_Config/Include"));
            foreach (var includeDirectory in ConfigurationIncludeDirectories)
            {
                if (Directory.Exists(includeDirectory))
                {
                    LoadAutoIncludeFiles(configPatcher, includeDirectory);
                }
            }
        }

        protected override void LoadAutoIncludeFiles(global::Sitecore.Configuration.ConfigPatcher patcher, string folder)
        {
            Assert.ArgumentNotNull((object)patcher, "patcher");
            Assert.ArgumentNotNull((object)folder, "folder");
            try
            {
                if (!Directory.Exists(folder) || folder.ToLowerInvariant() == Path.Combine(ConfigurationRootDirectory, ConnectionStringsFolderName).ToLowerInvariant()
                    || folder.ToLowerInvariant() == Path.Combine(ConfigurationRootDirectory, MailsettingsFolderName).ToLowerInvariant()
                    || folder.ToLowerInvariant() == Path.Combine(ConfigurationRootDirectory, "exclude").ToLowerInvariant())
                    return;

                var includeFolder = Path.Combine(ConfigurationRootDirectory, "Include").ToLowerInvariant();
                var files = Directory.GetFiles(folder, "*.config");
                Array.Sort(files);
                foreach (string file in files)
                {
                    try
                    {
                        if (_excludes.Contains(file.ToLowerInvariant().Replace(includeFolder, "").TrimStart('/', '\\')))
                        {
                            Trace.TraceInformation("File is in exclude list, skipping file '{0}'", file);
                        }
                        else if ((File.GetAttributes(file) & FileAttributes.Hidden) != (FileAttributes) 0)
                        {
                            Trace.TraceInformation("File is hidden, skipping file '{0}'", file);
                        }
                        else
                        {
                            patcher.ApplyPatch(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new global::Sitecore.Exceptions.ConfigurationException(string.Format("Could not load configuration file: {0}.", (object)file), ex);
                    }
                }
                var folders = Directory.GetDirectories(folder);
                Array.Sort(folders);
                foreach (string directory in folders)
                {
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.Hidden) == (FileAttributes)0)
                            this.LoadAutoIncludeFiles(patcher, directory);
                    }
                    catch (Exception ex)
                    {
                        throw new global::Sitecore.Exceptions.ConfigurationException(string.Format("Could not scan configuration folder {0} for files.", (object)directory), ex);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new global::Sitecore.Exceptions.ConfigurationException(string.Format("Could not scan configuration folder {0} for files.", (object)folder), ex);
            }
        }


        public static String ConfigurationRootDirectory
        {
            get
            {
                var environmentRoot = HostingEnvironment.MapPath("/App_Config");
                if (!String.IsNullOrEmpty(environmentRoot))
                    return environmentRoot;

                var configurationFile = new FileInfo(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);
                if (!String.IsNullOrEmpty(configurationFile.DirectoryName))
                    return Path.Combine(configurationFile.DirectoryName, "App_Config");
                else
                    return Path.Combine(ApplicationRootDirectrory, "App_Config");
            }
        }

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
                extension = extension.Substring(extension.IndexOf('-') + 1);
            return Guid.TryParse(extension, out temp);
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
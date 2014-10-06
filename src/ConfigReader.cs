using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net.Configuration;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Configuration;
using System.Web.Hosting;
using System.Xml;
using Sitecore;
using Sitecore.Configuration;
using Sitecore.Diagnostics;
using Sitecore.Xml.Patch;

namespace Efocus.Sitecore.ConditionalConfig
{
    public class ConfigReader : global::Sitecore.Configuration.ConfigReader, IConfigurationSectionHandler
    {
        //we need to have a _section variable, because sitecore's log4net.Appender.ConfigReader access it through reflection :s
        private XmlNode _section;
        private static readonly FieldInfo sectionField = typeof(global::Sitecore.Configuration.ConfigReader).GetField("_section", BindingFlags.NonPublic | BindingFlags.Instance);

        private static Dictionary<string, string> conditions = new Dictionary<string, string>()
            {
                {"machineName", Environment.MachineName},
                {"rootPath", HostingEnvironment.MapPath("/")}
            };
        object IConfigurationSectionHandler.Create(object parent, object configContext, XmlNode section)
        {
            //set default datafolder (convention over configuration)
            var dataFolderElement = section.OwnerDocument.CreateElement("sc.variable");
            dataFolderElement.SetAttribute("name", "dataFolder");
            dataFolderElement.SetAttribute("value", Path.GetFullPath(Path.Combine(HostingEnvironment.MapPath("/"), "..\\Data")));
            //section.InsertBefore(dataFolderElement, section.FirstChild);
            section.AppendChild(dataFolderElement);
            
            //Expand auto include-Condional
            LoadAutoIncludeFiles(section, HostingEnvironment.MapPath("/App_Config/Include-conditional/"));

            //var configFile = string.Format()"OTAP/Web/{0}.config", otapNode.Attribute("inherit").Value;

            sectionField.SetValue(this,  section);
            _section = section;

            return this;
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
                        if ((File.GetAttributes(str) & FileAttributes.Hidden) == (FileAttributes) 0)
                        {
                            var includeFile = new XmlDocument();
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

        private XmlPatcher _patcher = new XmlPatcher("http://www.sitecore.net/xmlconfig/set/", "http://www.sitecore.net/xmlconfig/");
        public void ApplyPatch(TextReader patch, XmlNode root)
        {
            var reader = new XmlTextReader(patch)
            {
                WhitespaceHandling = WhitespaceHandling.None
            };
            reader.MoveToContent();
            reader.ReadStartElement("configuration");
            this._patcher.Merge(root, new XmlReaderSource(reader));
            reader.ReadEndElement();
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
            deserializeMethod.Invoke(setting, new object[] {new XmlNodeReader(element)});
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
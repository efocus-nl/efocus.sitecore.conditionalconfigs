using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Sitecore.Xml.Patch;

namespace Efocus.Sitecore.ConditionalConfig
{
    public class ConfigPatcher : global::Sitecore.Configuration.ConfigPatcher
    {
        public ConfigPatcher(XmlNode node) : base(node)
        {
        }

        public ConfigPatcher(XmlNode node, XmlPatcher xmlPatcher) : base(node, xmlPatcher)
        {
        }

        private static Dictionary<string, string> conditions;
        private static Dictionary<string, string> Conditions
        {
            get
            {
                if (conditions != null)
                    return conditions;
                conditions =  new Dictionary<string, string>()
                {
                    {"machineName", Environment.MachineName},
                    {"rootPath", ConfigReader.ApplicationRootDirectrory}
                };
                var env = Environment.GetEnvironmentVariables();
                foreach (var k in env.Keys.OfType<string>())
                {
                    if (!conditions.ContainsKey(k))
                        conditions.Add(k, env[k] + "");
                }

                foreach (var appsetting in ConfigurationManager.AppSettings.AllKeys)
                {
                    if (!conditions.ContainsKey(appsetting))
                        conditions.Add(appsetting, ConfigurationManager.AppSettings[appsetting]);
                }
                return conditions;
            }
        }


        public static bool IsElementMatch(XmlNode node, string fileName)
        {
            var isMatch = false;
            var hasCondition = false;
            foreach (XmlAttribute attr in node.Attributes)
            {
                if (attr.Name.StartsWith("condition-"))
                {
                    hasCondition = true;
                    var isNotOperator = attr.Name.StartsWith("condition-not-");
                    var valueName = attr.Name.Substring(isNotOperator ? "condition-not-".Length : "condition-".Length);
                    if (Conditions.ContainsKey(valueName))
                    {
                        var value = Conditions[valueName];
                        var regex = new Regex(attr.Value);
                        isMatch = isNotOperator ? !regex.IsMatch(value) : regex.IsMatch(value);
                        if (!isMatch)
                        {
                            Trace.TraceInformation(
                                "Condition '{0}=\"{2}\"' is no match with '{1}', skipping file '{3}'", valueName,
                                value, attr.Value, fileName);
                            return false;
                        }
                    }
                    else
                    {
                        Trace.TraceInformation("Condition '{0}' is not a valid condition (unknown). {2} file '{1}'", valueName, fileName, isNotOperator ? "Not-operator, so inlcuding": "skipping");
                        if (!isNotOperator)
                        {
                            return false;
                        }
                        else
                        {
                            isMatch = true;
                        }
                    }
                }
            }
            return !hasCondition || isMatch;
        }

        public override void ApplyPatch(TextReader patch, string sourceName)
        {
            XmlTextReader xmlTextReader = new XmlTextReader(patch);
            xmlTextReader.WhitespaceHandling = WhitespaceHandling.None;
            int content = (int)xmlTextReader.MoveToContent();
            XmlNode rootNode;
            var element = this.GetXmlElement((XmlReader) xmlTextReader, sourceName, out rootNode);
            if (IsElementMatch(rootNode, sourceName))
                Patcher.Merge(Document, element);
        }

        protected override IXmlElement GetXmlElement(XmlReader reader, string sourceName)
        {
            XmlNode dummy;
            return GetXmlElement(reader, sourceName, out dummy);
        }
        public IXmlElement GetXmlElement(XmlReader reader, string sourceName, out XmlNode rootNode)
        {
            XmlDocument xmlDocument = new XmlDocument();
            xmlDocument.Load(reader);
            rootNode = xmlDocument.SelectSingleNode("configuration");
            if (rootNode != null)
                return (IXmlElement)new XmlDomSource(rootNode.SelectSingleNode("sitecore"), sourceName);
            rootNode = xmlDocument.SelectSingleNode("sitecore");
            if (rootNode == null)
                throw new Exception(string.Format("Cannot read patch file '{0}'. <sitecore> node is missing.", (object)(sourceName ?? "UNKNOWN")));
            return (IXmlElement)new XmlDomSource(rootNode, sourceName);
        }
    }
}
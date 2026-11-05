using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using AlgernonCommons.Notifications;
using AlgernonCommons.XML;

namespace CSLModernMap.Config
{
    /// <summary>
    /// XMLFileUtils 保存时创建新实例，属性必须绑定静态设置状态。
    /// 保留原配置文件名及 AlgernonCommons 的语言和更新通知字段。
    /// </summary>
    public class CSLModernMapConfig : SettingsXMLBase
    {
        /// <summary>设置文件名（相对游戏工作目录）。沿用旧版路径，避免丢配置。</summary>
        public const string SettingsFileName = "CSLModernMapConfig.xml";

        // 设置全局状态（静态字段初始值即默认值；构造函数不得改动这些值）。
        private static string s_mapOutputPath = string.Empty;

        /// <summary>地图文件输出路径（空 = 游戏工作目录下的 CSLModernMap 文件夹）。</summary>
        [XmlElement("MapOutputPath")]
        public string MapOutputPath
        {
            get => s_mapOutputPath;
            set => s_mapOutputPath = value ?? string.Empty;
        }

        /// <summary>输出目录（绝对路径）。</summary>
        public string GetOutputDirPath()
        {
            return Path.GetFullPath(string.IsNullOrEmpty(s_mapOutputPath) ? "CSLModernMap" : s_mapOutputPath);
        }

        /// <summary>设置由 LoadSettings 读入；返回绑定同一静态状态的配置视图。</summary>
        public static CSLModernMapConfig LoadOrDefault()
        {
            return new CSLModernMapConfig();
        }

        /// <summary>仅在尚无 WhatsNewVersion 时迁移旧 LastSeenVersion，由调用方保存。</summary>
        public static void MigrateLegacyLastSeenVersion()
        {
            try
            {
                // WhatsNew 内部默认版本为 0.0；非 0.0 说明新设置文件已带 WhatsNewVersion。
                if (!string.Equals(WhatsNew.LastNotifiedVersionString, "0.0", StringComparison.Ordinal))
                {
                    return;
                }

                if (!File.Exists(SettingsFileName))
                {
                    return;
                }

                XmlDocument doc = new XmlDocument();
                doc.Load(SettingsFileName);
                XmlNode node = doc.SelectSingleNode("//LastSeenVersion");
                if (node == null)
                {
                    return;
                }

                string lastSeen = node.InnerText == null ? string.Empty : node.InnerText.Trim();
                if (string.IsNullOrEmpty(lastSeen))
                {
                    return;
                }

                // setter 内部用 new Version() 解析，非法值会被安全忽略（保持原状）。
                WhatsNew.LastNotifiedVersionString = lastSeen;
            }
            catch (Exception)
            {
                // 迁移失败不影响游戏启动；最坏情况是老用户再看一次更新说明。
            }
        }
    }
}

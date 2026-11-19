using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using AlgernonCommons.Notifications;
using AlgernonCommons.XML;

namespace CSLModernMap.Config
{
    /// <summary>
    /// CSLModernMap 模组配置，序列化为 CSLModernMapConfig.xml（游戏工作目录相对路径，
    /// 与旧版同位置同名，保证现有用户设置无缝兼容）。
    ///
    /// 继承 AlgernonCommons.XML.SettingsXMLBase 后，设置文件自动携带
    /// Language / WhatsNewVersion / DetailedLogging 三个标准字段；
    /// 其中 WhatsNewVersion 供 WhatsNew 更新通知体系记录「不再显示」的版本号。
    ///
    /// 约定（AlgernonCommons 模式）：XML 文件类只是序列化载体——
    /// XMLFileUtils.Save 内部用 new TFile() 现造实例再序列化，属性 getter
    /// 必须读取全局状态；因此本模组自身的设置项存放在静态字段里。
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

        private static bool s_satelliteEnabled;
        [XmlElement("SatelliteEnabled")]
        public bool SatelliteEnabled { get => s_satelliteEnabled; set => s_satelliteEnabled = value; }

        /// <summary>输出目录（绝对路径）。</summary>
        public string GetOutputDirPath()
        {
            return Path.GetFullPath(string.IsNullOrEmpty(s_mapOutputPath) ? "CSLModernMap" : s_mapOutputPath);
        }

        /// <summary>
        /// 兼容旧调用点的加载入口。设置已在模组 OnEnabled → LoadSettings 时由
        /// XMLFileUtils.Load 读入全局状态，这里直接返回绑定全局状态的实例视图，行为不变。
        /// </summary>
        public static CSLModernMapConfig LoadOrDefault()
        {
            return new CSLModernMapConfig();
        }

        /// <summary>保存当前设置（保留旧接口，兼容历史调用点）。</summary>
        public void Save()
        {
            XMLFileUtils.Save<CSLModernMapConfig>(SettingsFileName);
        }

        /// <summary>
        /// 一次性迁移：把旧版配置里的 LastSeenVersion 写入 AlgernonCommons WhatsNew
        /// 的「最后已通知版本」。
        ///
        /// 背景：旧版 UpdateNotice 把已看版本记在 &lt;LastSeenVersion&gt; 元素；
        /// 新设置类没有该属性（XmlSerializer 会静默忽略未知元素），若不迁移，
        /// 老用户升级后会被重复弹已经看过的更新说明。
        ///
        /// 只在 WhatsNewVersion 尚未写入（首次升级运行）时执行；迁移结果由调用方
        /// （CSLModernMap.LoadSettings）立即保存。
        /// </summary>
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

namespace EasyChat.Presentation.Features.Settings;

/// <summary>
/// Field-level settings search. Empty query matches everything; otherwise any token
/// (space-separated keywords or the section header) must contain the query.
/// </summary>
public static class SettingsSearch
{
    public const string GeneralAutoStartFields =
        "auto start startup launch login windows macos linux 开机自启 登录启动";

    public static bool Matches(string? query, string? keywords)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        if (string.IsNullOrWhiteSpace(keywords))
            return false;

        var needle = query.Trim();
        return keywords.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesAny(string? query, params string?[] bags)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        foreach (var bag in bags)
        {
            if (Matches(query, bag))
                return true;
        }

        return false;
    }

    // Section keyword bags include field tokens so section chrome stays visible
    // when a nested field hits.
    public const string GeneralFields =
        "display language native closing behavior exit application data proxy url ocr models asr models import delete model language 显示语言 母语 本地语言 退出方式 应用数据 代理地址 OCR 模型 语音识别 导入 删除";
    public const string GeneralSearchFields = GeneralFields + " " + GeneralAutoStartFields;
    public const string TranslationFields =
        "model ai engine key baidu tencent google deepl api proxy 模型 翻译 密钥 引擎";
    public const string SelectionFields =
        "selection translation enable translate correct correction polish summary explanation trigger engine using ai model machine translation prompt selection toolbar 划词工具栏 翻译 启用 纠错 润色 总结 解释 取词方式 触发 引擎 大模型 AI 模型 机器翻译 提示词";
    public const string TtsFields =
        "tts provider configure voices voice 语音 提供商 配置语音 配置 音色";
    public const string ScreenshotFields =
        "screenshot mode ocr recognition idle timeout fixed area settings capture precise 截图模式 OCR 模式 闲置关闭时间 固定区域设置 精准";
    public const string ResultFields =
        "result window mode read aloud font size family color transparency background enable auto read delay auto close ms per char close 结果窗口模式 朗读设置 字体大小 字体名称 颜色 透明 背景 自动计算延迟 自动关闭延迟 毫秒/字 延迟 关闭";
    public const string InputFields =
        "input delivery mode paste type key send delay reverse translate language transparency background font delay 文本投递模式 按键发送延迟 反向翻译 输入 投递 粘贴 消息 发送 延迟 反转 翻译 语言 透明 背景 字体";
}

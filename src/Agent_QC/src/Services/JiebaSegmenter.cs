using JiebaNet.Segmenter;

namespace Agent_QC.Services;

/// <summary>
/// 基于 jieba.NET 的中文分词器，加载放射科自定义词典，提供线程安全的分词服务。
/// </summary>
public class JiebaSegmenter
{
    private readonly JiebaNet.Segmenter.JiebaSegmenter _segmenter;

    static JiebaSegmenter()
    {
        // jieba.NET 需要内置词典文件（prob_trans.json 等），尝试多个位置
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages", "jieba.net", "0.42.2", "Resources"),
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir))
            {
                ConfigManager.ConfigFileBaseDir = dir;
                break;
            }
        }
    }

    public JiebaSegmenter(string dictPath)
    {
        _segmenter = new JiebaNet.Segmenter.JiebaSegmenter();

        if (!File.Exists(dictPath))
            return;

        foreach (var line in File.ReadLines(dictPath))
        {
            var word = line.Trim();
            if (!string.IsNullOrEmpty(word) && !word.StartsWith('#'))
                _segmenter.AddWord(word);
        }
    }

    /// <summary>对中文文本进行分词，返回词元列表。空输入返回空列表。</summary>
    public List<string> Segment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return _segmenter.Cut(text).ToList();
    }
}

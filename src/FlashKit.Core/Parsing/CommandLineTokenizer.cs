using System.Text;

namespace FlashKit.Core.Parsing;

/// <summary>
/// 终端只允许 adb / fastboot 两个前缀，解析阶段不经过 cmd.exe 或 PowerShell，
/// 因此引号、空格路径和常见 shell 注入符号都不会被本机 shell 解释。
/// </summary>
public static class CommandLineTokenizer
{
    public static IReadOnlyList<string> Tokenize(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';

        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];
            if (inQuotes)
            {
                if (ch == quoteChar)
                {
                    inQuotes = false;
                }
                else if (ch == '\\' && i + 1 < commandLine.Length &&
                         (commandLine[i + 1] == quoteChar || commandLine[i + 1] == '\\'))
                {
                    current.Append(commandLine[++i]);
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (inQuotes)
        {
            throw new FormatException("命令中的引号没有闭合");
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    public static bool IsAllowedExecutable(string token) =>
        token.Equals("adb", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("fastboot", StringComparison.OrdinalIgnoreCase);
}

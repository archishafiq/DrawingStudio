using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DrawingStudio.Shared
{
    /// <summary>Token-based naming and ISO 19650 sheet numbering.</summary>
    public static class Naming
    {
        private static readonly Regex TokenRx = new Regex(@"\{(\w+)\}", RegexOptions.Compiled);

        // Characters Revit rejects in view names and sheet numbers.
        private static readonly char[] Invalid = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        public static string Fill(string template, IDictionary<string, string> tokens)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;
            var result = TokenRx.Replace(template, m =>
                tokens.TryGetValue(m.Groups[1].Value, out var v) ? (v ?? string.Empty) : m.Value);
            // collapse doubled separators left behind by empty tokens
            result = Regex.Replace(result, @"\s+-\s+-\s+", " - ");
            result = Regex.Replace(result, @"\s{2,}", " ");
            return result.Trim(' ', '-');
        }

        public static string Clean(string name, bool uppercase)
        {
            if (name == null) return string.Empty;
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(Array.IndexOf(Invalid, c) >= 0 ? '-' : c);
            var s = sb.ToString().Trim();
            return uppercase ? s.ToUpperInvariant() : s;
        }

        public static string MakeUnique(string name, ISet<string> existing)
        {
            if (!existing.Contains(name)) { existing.Add(name); return name; }
            for (int i = 2; ; i++)
            {
                var candidate = name + " (" + i + ")";
                if (!existing.Contains(candidate)) { existing.Add(candidate); return candidate; }
            }
        }

        /// <summary>
        /// ISO 19650 level/location code from a Revit level name:
        /// "Ground Floor" → 00, "Level 3" → 03, "Basement 2" / "B2" / "-2" → B2, "Roof" → RF.
        /// </summary>
        public static string LevelCode(string levelName, string fallback = "ZZ")
        {
            if (string.IsNullOrWhiteSpace(levelName)) return fallback;
            var n = levelName.Trim().ToUpperInvariant();

            if (Regex.IsMatch(n, @"\b(GROUND|GF|G\.F\.?)\b")) return "00";
            if (Regex.IsMatch(n, @"\b(ROOF|RF)\b")) return "RF";
            if (Regex.IsMatch(n, @"\b(FOUNDATION|FDN|FOOTING)\b")) return "FD";

            var m = Regex.Match(n, @"\b(?:BASEMENT|BSMT|B)\s*-?\s*(\d+)");
            if (m.Success) return "B" + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            m = Regex.Match(n, @"\b(?:MEZZANINE|MEZZ|MZ)\s*-?\s*(\d*)");
            if (m.Success) return "M" + (m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "1");

            string[] ordinals = { "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "SIXTH", "SEVENTH", "EIGHTH", "NINTH", "TENTH" };
            for (int i = 0; i < ordinals.Length; i++)
                if (Regex.IsMatch(n, @"\b" + ordinals[i] + @"\b")) return (i + 1).ToString("00", CultureInfo.InvariantCulture);

            m = Regex.Match(n, @"(-?\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                return num < 0 ? "B" + Math.Abs(num) : num.ToString("00", CultureInfo.InvariantCulture);

            return fallback;
        }

        public static Dictionary<string, string> SheetTokens(StandardsConfig c, string levelCode, int number) =>
            new Dictionary<string, string>
            {
                ["Project"] = c.ProjectCode,
                ["Originator"] = c.Originator,
                ["Volume"] = c.Volume,
                ["Level"] = string.IsNullOrWhiteSpace(levelCode) ? c.DefaultLevelCode : levelCode,
                ["Type"] = c.InfoType,
                ["Role"] = c.Role,
                ["Number"] = number.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, c.NumberDigits), '0')
            };

        public static string SheetNumber(StandardsConfig c, string levelCode, int number) =>
            Clean(Fill(c.SheetNumberTemplate, SheetTokens(c, levelCode, number)), true);

        /// <summary>Next free sheet number for this level, skipping numbers already used.</summary>
        public static string NextSheetNumber(StandardsConfig c, string levelCode, ISet<string> used)
        {
            int step = Math.Max(1, c.NumberStep);
            for (int n = Math.Max(0, c.StartNumber); n < 1000000; n += step)
            {
                var candidate = SheetNumber(c, levelCode, n);
                if (!used.Contains(candidate)) { used.Add(candidate); return candidate; }
            }
            throw new InvalidOperationException("No free sheet number available.");
        }

        /// <summary>Regex that validates sheet numbers against the template (used by QA).</summary>
        public static string SheetNumberPattern(StandardsConfig c) => BuildPattern(c, named: false);

        /// <summary>Reads the token values back out of an existing sheet number.</summary>
        public static Dictionary<string, string> ParseSheetNumber(StandardsConfig c, string sheetNumber)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(sheetNumber)) return result;
            var m = Regex.Match(sheetNumber, BuildPattern(c, named: true), RegexOptions.IgnoreCase);
            if (!m.Success) return result;
            foreach (var token in new[] { "Project", "Originator", "Volume", "Level", "Type", "Role", "Number" })
                if (m.Groups[token].Success) result[token] = m.Groups[token].Value;
            return result;
        }

        private static string BuildPattern(StandardsConfig c, bool named)
        {
            var template = c.SheetNumberTemplate ?? string.Empty;
            var sb = new StringBuilder("^");
            int pos = 0;
            var used = new HashSet<string>();
            foreach (Match m in TokenRx.Matches(template))
            {
                sb.Append(Regex.Escape(template.Substring(pos, m.Index - pos)));
                var token = m.Groups[1].Value;
                string body;
                switch (token)
                {
                    case "Number": body = @"\d{" + Math.Max(1, c.NumberDigits) + "}"; break;
                    case "Project": body = Regex.Escape(c.ProjectCode ?? ""); break;
                    case "Originator": body = Regex.Escape(c.Originator ?? ""); break;
                    case "Role": body = "[A-Z0-9]{1,3}"; break;
                    case "Type": body = "[A-Z0-9]{2}"; break;
                    default: body = "[A-Z0-9]{1,6}"; break;
                }
                sb.Append(named && used.Add(token) ? "(?<" + token + ">" + body + ")" : "(?:" + body + ")");
                pos = m.Index + m.Length;
            }
            sb.Append(Regex.Escape(template.Substring(pos)));
            sb.Append("$");
            return sb.ToString();
        }

        public static string PlanKindLabel(string planKind)
        {
            switch (planKind)
            {
                case "StructuralPlan": return "STRUCTURAL PLAN";
                case "CeilingPlan": return "REFLECTED CEILING PLAN";
                default: return "FLOOR PLAN";
            }
        }

        public static string ScaleLabel(int scale) => scale > 0 ? "1-" + scale.ToString(CultureInfo.InvariantCulture) : "";

        /// <summary>Natural sort key so "Grid 10" follows "Grid 9".</summary>
        public static string NaturalKey(string s) =>
            Regex.Replace(s ?? string.Empty, @"\d+", m => m.Value.PadLeft(10, '0'));

        public static IEnumerable<T> NaturalOrder<T>(this IEnumerable<T> items, Func<T, string> key) =>
            items.OrderBy(i => NaturalKey(key(i)), StringComparer.OrdinalIgnoreCase);
    }
}

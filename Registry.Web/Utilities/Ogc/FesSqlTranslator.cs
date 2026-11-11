using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using Registry.Web.Exceptions;

namespace Registry.Web.Utilities.Ogc;

/// <summary>
/// What a FES 2.0 filter becomes when DroneDB can evaluate it: an SQL expression over the layer's
/// fields (<see cref="Ports.VectorQuery.Where"/>) and, for a BBOX operator, a bounding box in lon/lat.
/// </summary>
/// <param name="Where">The attribute filter, or null when the filter is a BBOX only.</param>
/// <param name="Bbox">[minLon, minLat, maxLon, maxLat] of a BBOX operator, or null.</param>
/// <param name="BboxCrs">CRS of <paramref name="Bbox"/>.</param>
public sealed record FesSqlFilter(string? Where, double[]? Bbox, string? BboxCrs);

/// <summary>
/// Translates FES 2.0 filters to SQL for DroneDB (SQLite on the layer's GeoPackage), so the
/// features are filtered, counted and paged where they are stored instead of fetching up to
/// 10 000 of them and evaluating the filter in the Registry.
///
/// <para>Translated: And, Or, Not, the six comparisons (matchCase="false" only for equality
/// with an ASCII literal), Between, Like (matchCase="true" as a case-sensitive GLOB,
/// "false" as LIKE when the pattern is ASCII), Null/Nil, and one BBOX on its own or as a term
/// of the top-level And. Literals are written as SQL strings: the column's type decides how they
/// compare (SQLite affinity), so numeric fields compare as numbers and text fields as text.</para>
///
/// <para>Anything else returns null and is evaluated by the Registry: ResourceId, functions,
/// geometry literals, matchAction="All", ddbSeq and gml:id references, properties that are not
/// fields of the layer, nested BBOX.</para>
/// </summary>
public static class FesSqlTranslator
{
    private const string NsFes = OgcNamespaces.Fes;

    /// <summary>
    /// The SQL form of <paramref name="filter"/> (a fes:Filter element, or a predicate), or null
    /// when part of it cannot be translated. <paramref name="fields"/> are the layer's field names.
    /// </summary>
    public static FesSqlFilter? Translate(XmlElement filter, IReadOnlyCollection<string> fields)
    {
        var predicate = filter.LocalName == "Filter" ? FirstElement(filter) : filter;
        if (predicate == null)
            return null;

        var context = new Context(fields);

        // A BBOX alone or as a term of the top-level And becomes the query's bbox
        if (IsFes(predicate, "BBOX"))
            return TryBbox(predicate, out var bbox, out var crs) ? new FesSqlFilter(null, bbox, crs) : null;

        if (IsFes(predicate, "And"))
        {
            double[]? bbox = null;
            string? crs = null;
            var terms = new List<string>();
            foreach (var child in Elements(predicate))
            {
                if (IsFes(child, "BBOX"))
                {
                    if (bbox != null || !TryBbox(child, out bbox, out crs))
                        return null;
                    continue;
                }

                var sql = context.Translate(child);
                if (sql == null)
                    return null;
                terms.Add(sql);
            }

            return terms.Count == 0 && bbox == null
                ? null
                : new FesSqlFilter(terms.Count == 0 ? null : string.Join(" AND ", terms), bbox, crs);
        }

        var where = context.Translate(predicate);
        return where == null ? null : new FesSqlFilter(where, null, null);
    }

    /// <summary>
    /// The property a fes:ValueReference names: its last path step without namespace prefix
    /// or predicate ("ddb:foo/ddb:bar[1]" → "bar"). Empty when the reference is empty.
    /// </summary>
    public static string PropertyName(string valueReference)
    {
        var path = valueReference.Trim();
        var step = path.Split('/').Last();
        var local = step.Contains(':') ? step[(step.IndexOf(':') + 1)..] : step;
        var bracket = local.IndexOf('[');
        return bracket > 0 ? local[..bracket] : local;
    }

    private sealed class Context(IReadOnlyCollection<string> fields)
    {
        public string? Translate(XmlElement predicate)
        {
            if (predicate.NamespaceURI != NsFes && predicate.NamespaceURI.Length > 0)
                return null;

            switch (predicate.LocalName)
            {
                case "And":
                case "Or":
                {
                    var terms = Elements(predicate).Select(Translate).ToList();
                    if (terms.Count == 0 || terms.Any(t => t == null))
                        return null;
                    return "(" + string.Join(predicate.LocalName == "And" ? " AND " : " OR ", terms) + ")";
                }
                case "Not":
                {
                    var operand = Elements(predicate).ToList();
                    if (operand.Count != 1)
                        return null;
                    var term = Translate(operand[0]);
                    return term == null ? null : "(NOT " + term + ")";
                }
                case "PropertyIsEqualTo":
                case "PropertyIsNotEqualTo":
                case "PropertyIsLessThan":
                case "PropertyIsGreaterThan":
                case "PropertyIsLessThanOrEqualTo":
                case "PropertyIsGreaterThanOrEqualTo":
                    return Comparison(predicate);
                case "PropertyIsBetween":
                    return Between(predicate);
                case "PropertyIsLike":
                    return Like(predicate);
                case "PropertyIsNull":
                case "PropertyIsNil":
                    return IsNull(predicate);
                default:
                    return null;
            }
        }

        private string? Comparison(XmlElement predicate)
        {
            if (string.Equals(predicate.GetAttribute("matchAction"), "All", StringComparison.OrdinalIgnoreCase))
                return null;

            var operands = Elements(predicate).ToList();
            if (operands.Count != 2)
                return null;

            // Ref op Literal, or Literal op Ref (the operator is then mirrored)
            var mirrored = operands[0].LocalName == "Literal";
            var reference = mirrored ? operands[1] : operands[0];
            var literal = mirrored ? operands[0] : operands[1];
            if (reference.LocalName != "ValueReference" || literal.LocalName != "Literal")
                return null;

            var column = Column(reference);
            var value = LiteralText(literal);
            if (column == null || value == null)
                return null;

            var op = (predicate.LocalName, mirrored) switch
            {
                ("PropertyIsEqualTo", _) => "=",
                ("PropertyIsNotEqualTo", _) => "<>",
                ("PropertyIsLessThan", false) or ("PropertyIsGreaterThan", true) => "<",
                ("PropertyIsGreaterThan", false) or ("PropertyIsLessThan", true) => ">",
                ("PropertyIsLessThanOrEqualTo", false) or ("PropertyIsGreaterThanOrEqualTo", true) => "<=",
                _ => ">="
            };

            var sql = column + " " + op + " " + Quote(value);
            if (!MatchCase(predicate))
            {
                // NOCASE folds ASCII letters only, and orders differently from the Registry
                if (op is not ("=" or "<>") || !IsAscii(value))
                    return null;
                sql += " COLLATE NOCASE";
            }

            // A null field compares to nothing (SQL NULL), as an absent value in the Registry's evaluator
            return sql;
        }

        private string? Between(XmlElement predicate)
        {
            XmlElement? reference = null;
            string? lower = null, upper = null;
            foreach (var e in Elements(predicate))
            {
                switch (e.LocalName)
                {
                    case "ValueReference": reference = e; break;
                    case "LowerBoundary": lower = BoundaryLiteral(e); break;
                    case "UpperBoundary": upper = BoundaryLiteral(e); break;
                    default: return null;
                }
            }

            var column = reference == null ? null : Column(reference);
            if (column == null || lower == null || upper == null)
                return null;
            return column + " BETWEEN " + Quote(lower) + " AND " + Quote(upper);
        }

        private string? Like(XmlElement predicate)
        {
            var wildCard = AttributeOr(predicate, "wildCard", "*");
            var singleChar = AttributeOr(predicate, "singleChar", "?");
            var escapeChar = AttributeOr(predicate, "escapeChar", "\\");
            if (wildCard.Length != 1 || singleChar.Length != 1 || escapeChar.Length != 1)
                return null;

            XmlElement? reference = null, literal = null;
            foreach (var e in Elements(predicate))
            {
                if (e.LocalName == "ValueReference") reference = e;
                else if (e.LocalName == "Literal") literal = e;
                else return null;
            }

            var column = reference == null ? null : Column(reference);
            var pattern = literal == null ? null : LiteralText(literal);
            if (column == null || pattern == null)
                return null;

            var matchCase = MatchCase(predicate);
            if (!matchCase && !IsAscii(pattern))
                return null;

            var sql = new StringBuilder();
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == escapeChar[0] && i + 1 < pattern.Length)
                    AppendLiteral(sql, pattern[++i], matchCase);
                else if (c == wildCard[0])
                    sql.Append(matchCase ? '*' : '%');
                else if (c == singleChar[0])
                    sql.Append(matchCase ? '?' : '_');
                else
                    AppendLiteral(sql, c, matchCase);
            }

            // GLOB is case-sensitive; LIKE folds ASCII letters
            return matchCase
                ? column + " GLOB " + Quote(sql.ToString())
                : column + " LIKE " + Quote(sql.ToString()) + " ESCAPE '\\'";
        }

        private string? IsNull(XmlElement predicate)
        {
            var operands = Elements(predicate).ToList();
            if (operands.Count != 1 || operands[0].LocalName != "ValueReference")
                return null;
            var column = Column(operands[0]);
            // The Registry's evaluator takes an empty value as null
            return column == null ? null : "(" + column + " IS NULL OR " + column + " = '')";
        }

        /// <summary>The quoted column a value reference names, or null when it is not a field.</summary>
        private string? Column(XmlElement reference)
        {
            var name = PropertyName(reference.InnerText);
            return name.Length > 0 && fields.Contains(name) ? QuoteIdentifier(name) : null;
        }
    }

    private static bool TryBbox(XmlElement bboxElement, out double[]? bbox, out string? crs)
    {
        bbox = null;
        crs = null;
        var envelope = Elements(bboxElement).FirstOrDefault(e => e.LocalName == "Envelope");
        if (envelope == null)
            return false;

        var lower = Elements(envelope).FirstOrDefault(e => e.LocalName == "lowerCorner")?.InnerText;
        var upper = Elements(envelope).FirstOrDefault(e => e.LocalName == "upperCorner")?.InnerText;
        var lo = lower?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hi = upper?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (lo is not { Length: 2 } || hi is not { Length: 2 })
            return false;

        var srsName = envelope.GetAttribute("srsName");
        try
        {
            // Same axis-order rules as the BBOX parameter of WFS 2.0
            var parsed = OgcRequestParser.ParseBbox($"{lo[0]},{lo[1]},{hi[0]},{hi[1]}",
                string.IsNullOrWhiteSpace(srsName) ? "EPSG:4326" : srsName, "2.0.0");
            bbox = parsed.Bbox;
            crs = parsed.Crs;
            return true;
        }
        catch (OgcException)
        {
            return false;
        }
    }

    private static string? BoundaryLiteral(XmlElement boundary)
    {
        var operands = Elements(boundary).ToList();
        return operands.Count == 1 && operands[0].LocalName == "Literal" ? LiteralText(operands[0]) : null;
    }

    /// <summary>The text of a scalar literal; null for one holding elements (a geometry).</summary>
    private static string? LiteralText(XmlElement literal) =>
        Elements(literal).Any() ? null : literal.InnerText;

    private static bool MatchCase(XmlElement predicate) =>
        !string.Equals(predicate.GetAttribute("matchCase"), "false", StringComparison.OrdinalIgnoreCase);

    private static string AttributeOr(XmlElement element, string name, string fallback)
    {
        var value = element.GetAttribute(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static void AppendLiteral(StringBuilder sql, char c, bool glob)
    {
        if (glob)
        {
            if (c is '*' or '?' or '[')
                sql.Append('[').Append(c).Append(']');
            else
                sql.Append(c);
        }
        else
        {
            if (c is '%' or '_' or '\\')
                sql.Append('\\');
            sql.Append(c);
        }
    }

    private static bool IsAscii(string s) => s.All(c => c < 128);

    private static string Quote(string literal) => "'" + literal.Replace("'", "''") + "'";

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static bool IsFes(XmlElement e, string localName) =>
        e.LocalName == localName && (e.NamespaceURI == NsFes || e.NamespaceURI.Length == 0);

    private static XmlElement? FirstElement(XmlElement parent) => Elements(parent).FirstOrDefault();

    private static IEnumerable<XmlElement> Elements(XmlElement parent) => parent.ChildNodes.OfType<XmlElement>();
}

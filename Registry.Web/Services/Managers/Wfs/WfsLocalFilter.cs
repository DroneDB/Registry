#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Registry.Web.Exceptions;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers.Wfs;

/// <summary>
/// The FES 2.0 filter evaluation WFS does itself (RQ-12, out of <c>WfsManager</c>), for the filters DroneDB's SQL
/// cannot express.
/// </summary>
internal static class WfsLocalFilter
{
    // =====================================================================================
    // FES 2.0 (ISO 19143) filter evaluator.
    // Supports the subset required by the OGC ETS for WFS 2.0 Basic conformance:
    //   - fes:ResourceId (rid)
    //   - Comparison: PropertyIsEqualTo / NotEqualTo / LessThan / GreaterThan /
    //                 LessThanOrEqualTo / GreaterThanOrEqualTo / Between / Like / Null / Nil
    //     (with matchCase, matchAction=Any|All)
    //   - Logical: And / Or / Not
    // Operates on the wfs:FeatureCollection XML string produced by WrapAsWfsFeatureCollection.
    // =====================================================================================
    private const string NsFes = OgcNamespaces.Fes;

    /// <summary>Parses a FES filter (fes:Filter, or a predicate, which is wrapped in one).</summary>
    internal static XmlElement ParseFesFilter(string filterXml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(filterXml);
            var filter = doc.DocumentElement
                         ?? throw new OgcException("OperationProcessingFailed", "Empty fes:Filter", 400, "Filter");
            if (filter.LocalName == "Filter")
                return filter;

            var wrap = doc.CreateElement("fes", "Filter", NsFes);
            wrap.AppendChild(filter.CloneNode(true));
            return wrap;
        }
        catch (XmlException ex)
        {
            throw new OgcException("OperationProcessingFailed", "Malformed fes:Filter: " + ex.Message, 400, "Filter");
        }
    }

    /// <summary>The wfs:member elements of <paramref name="collection"/> whose feature matches the filter.</summary>
    internal static List<XmlElement> FilterMembers(XmlDocument collection, XmlElement filter)
    {
        var matches = new List<XmlElement>();
        var predicate = filter.ChildNodes.OfType<XmlElement>().FirstOrDefault();
        var root = collection.DocumentElement;
        if (root == null)
            return matches;

        // FES 2.0 §8.3: references outside the service's namespaces are InvalidParameterValue
        if (predicate != null)
            ValidateValueReferences(predicate);

        foreach (var member in Members(root))
        {
            var feature = FeatureOf(member);
            if (feature == null)
                continue;
            if (predicate == null)
            {
                matches.Add(member);
                continue;
            }

            bool match;
            try
            {
                match = EvalPredicate(predicate, feature);
            }
            catch (OgcException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WFS EvalPredicate: error evaluating predicate ({ex.Message}); excluding feature");
                match = false;
            }
            if (match) matches.Add(member);
        }
        return matches;
    }

    /// <summary>
    /// <paramref name="collection"/> with only the page [startIndex, startIndex + count) of
    /// <paramref name="matches"/>: numberMatched counts every match, numberReturned the page. The
    /// features keep the ddbSeq of the unfiltered collection, which filters may have referenced.
    /// </summary>
    internal static string PageMembers(XmlDocument collection, List<XmlElement> matches, int startIndex, int count)
    {
        var root = collection.DocumentElement!;
        var page = matches.Skip(startIndex).Take(count).ToHashSet();
        foreach (var member in Members(root).ToList())
            if (!page.Contains(member))
                root.RemoveChild(member);
        root.SetAttribute("numberReturned", page.Count.ToString(CultureInfo.InvariantCulture));
        root.SetAttribute("numberMatched", matches.Count.ToString(CultureInfo.InvariantCulture));

        using var ms = new MemoryStream();
        using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) }))
            collection.Save(xw);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static IEnumerable<XmlElement> Members(XmlElement root) =>
        root.ChildNodes.OfType<XmlElement>().Where(m => m.LocalName == "member" && m.NamespaceURI == OgcNamespaces.Wfs);

    internal static XmlElement? FeatureOf(XmlElement member) => member.ChildNodes.OfType<XmlElement>().FirstOrDefault();

    /// <summary>Evaluate a FES predicate element against a feature element.</summary>
    private static bool EvalPredicate(XmlElement pred, XmlElement feature)
    {
        if (pred.NamespaceURI != NsFes)
            // Allow callers to pass FES elements without namespace declared explicitly.
            if (pred.LocalName is not ("And" or "Or" or "Not"
                or "PropertyIsEqualTo" or "PropertyIsNotEqualTo"
                or "PropertyIsLessThan" or "PropertyIsGreaterThan"
                or "PropertyIsLessThanOrEqualTo" or "PropertyIsGreaterThanOrEqualTo"
                or "PropertyIsLike" or "PropertyIsNull" or "PropertyIsNil"
                or "PropertyIsBetween" or "ResourceId"))
                throw new OgcException("OperationProcessingFailed",
                    $"Unsupported filter predicate '{pred.LocalName}'", 400, "Filter");

        switch (pred.LocalName)
        {
            case "And":
                foreach (XmlNode c in pred.ChildNodes)
                    if (c is XmlElement ce && !EvalPredicate(ce, feature)) return false;
                return true;
            case "Or":
                foreach (XmlNode c in pred.ChildNodes)
                    if (c is XmlElement ce && EvalPredicate(ce, feature)) return true;
                return false;
            case "Not":
                foreach (XmlNode c in pred.ChildNodes)
                    if (c is XmlElement ce) return !EvalPredicate(ce, feature);
                return false;
            case "ResourceId":
            {
                var rid = pred.GetAttribute("rid");
                var gid = feature.GetAttribute("id", "http://www.opengis.net/gml/3.2");
                return !string.IsNullOrEmpty(rid) && rid == gid;
            }
            case "PropertyIsEqualTo":
            case "PropertyIsNotEqualTo":
            case "PropertyIsLessThan":
            case "PropertyIsGreaterThan":
            case "PropertyIsLessThanOrEqualTo":
            case "PropertyIsGreaterThanOrEqualTo":
                return EvalComparison(pred, feature);
            case "PropertyIsBetween":
                return EvalBetween(pred, feature);
            case "PropertyIsLike":
                return EvalLike(pred, feature);
            case "PropertyIsNull":
            case "PropertyIsNil":
                return EvalIsNull(pred, feature);
            default:
                throw new OgcException("OperationProcessingFailed",
                    $"Unsupported filter predicate '{pred.LocalName}'", 400, "Filter");
        }
    }

    private static bool EvalComparison(XmlElement pred, XmlElement feature)
    {
        var matchCase = !string.Equals(pred.GetAttribute("matchCase"), "false", StringComparison.OrdinalIgnoreCase);
        var matchAction = pred.GetAttribute("matchAction");
        if (string.IsNullOrEmpty(matchAction)) matchAction = "Any";

        // Reject geometric operands inside Literal: comparison operators take simple scalar
        // literals only (FES 2.0 §7.7). E.g. <Literal><gml:Envelope/></Literal> is invalid.
        foreach (XmlNode c in pred.ChildNodes)
            if (c is XmlElement le && le.LocalName == "Literal")
                foreach (XmlNode cc in le.ChildNodes)
                    if (cc is XmlElement ce && ce.NamespaceURI == "http://www.opengis.net/gml/3.2")
                        throw new OgcException("OperationProcessingFailed",
                            $"Geometric value '{ce.LocalName}' is not a valid operand for {pred.LocalName}",
                            400, "Literal");

        var (lefts, rights) = GetOperands(pred, feature);
        if (rights.Count == 0) return false;
        var op = pred.LocalName;

        bool Compare(string a, string b)
        {
            // Numeric compare if both parse as double, else string.
            if (double.TryParse(a, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var da)
                && double.TryParse(b, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var db))
            {
                return op switch
                {
                    "PropertyIsEqualTo" => da == db,
                    "PropertyIsNotEqualTo" => da != db,
                    "PropertyIsLessThan" => da < db,
                    "PropertyIsGreaterThan" => da > db,
                    "PropertyIsLessThanOrEqualTo" => da <= db,
                    "PropertyIsGreaterThanOrEqualTo" => da >= db,
                    _ => false
                };
            }
            var ca = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var cmp = string.Compare(a, b, ca);
            return op switch
            {
                "PropertyIsEqualTo" => cmp == 0,
                "PropertyIsNotEqualTo" => cmp != 0,
                "PropertyIsLessThan" => cmp < 0,
                "PropertyIsGreaterThan" => cmp > 0,
                "PropertyIsLessThanOrEqualTo" => cmp <= 0,
                "PropertyIsGreaterThanOrEqualTo" => cmp >= 0,
                _ => false
            };
        }

        bool any = false, all = true;
        foreach (var l in lefts)
            foreach (var r in rights)
            {
                if (Compare(l, r)) any = true;
                else all = false;
            }
        return string.Equals(matchAction, "All", StringComparison.OrdinalIgnoreCase) ? all : any;
    }

    private static bool EvalBetween(XmlElement pred, XmlElement feature)
    {
        XmlElement? value = null, lower = null, upper = null;
        foreach (XmlNode c in pred.ChildNodes)
            if (c is XmlElement e)
                switch (e.LocalName)
                {
                    case "ValueReference": case "Function": case "Literal": value ??= e; break;
                    case "LowerBoundary": lower = e; break;
                    case "UpperBoundary": upper = e; break;
                }
        if (value == null || lower == null || upper == null) return false;
        var leftVals = ResolveValues(value, feature);
        var lo = ResolveLiteralOrRef(lower, feature);
        var hi = ResolveLiteralOrRef(upper, feature);
        foreach (var v in leftVals)
        {
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)
                && double.TryParse(lo, NumberStyles.Float, CultureInfo.InvariantCulture, out var dlo)
                && double.TryParse(hi, NumberStyles.Float, CultureInfo.InvariantCulture, out var dhi))
            {
                if (dv >= dlo && dv <= dhi) return true;
            }
            else if (string.Compare(v, lo, StringComparison.Ordinal) >= 0
                  && string.Compare(v, hi, StringComparison.Ordinal) <= 0) return true;
        }
        return false;
    }

    private static string ResolveLiteralOrRef(XmlElement bound, XmlElement feature)
    {
        foreach (XmlNode c in bound.ChildNodes)
            if (c is XmlElement e)
            {
                if (e.LocalName == "Literal") return e.InnerText;
                if (e.LocalName == "ValueReference")
                {
                    var vals = ResolveValues(e, feature);
                    return vals.FirstOrDefault() ?? "";
                }
            }
        return bound.InnerText;
    }

    private static bool EvalLike(XmlElement pred, XmlElement feature)
    {
        var wildcard = pred.GetAttribute("wildCard"); if (string.IsNullOrEmpty(wildcard)) wildcard = "*";
        var single = pred.GetAttribute("singleChar"); if (string.IsNullOrEmpty(single)) single = "?";
        var esc = pred.GetAttribute("escapeChar"); if (string.IsNullOrEmpty(esc)) esc = "\\";
        var matchCase = !string.Equals(pred.GetAttribute("matchCase"), "false", StringComparison.OrdinalIgnoreCase);

        XmlElement? refEl = null, litEl = null;
        foreach (XmlNode c in pred.ChildNodes)
            if (c is XmlElement e)
            {
                if (e.LocalName == "ValueReference") refEl = e;
                else if (e.LocalName == "Literal") litEl = e;
            }
        if (refEl == null || litEl == null) return false;
        var vals = ResolveValues(refEl, feature);
        if (vals.Count == 0) return false;
        var pattern = LikeToRegex(litEl.InnerText, wildcard, single, esc);
        var opts = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
        var rx = new Regex(pattern, opts);
        foreach (var v in vals) if (rx.IsMatch(v)) return true;
        return false;
    }

    private static string LikeToRegex(string like, string wildcard, string single, string escape)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < like.Length; i++)
        {
            var ch = like[i].ToString();
            if (ch == escape && i + 1 < like.Length)
            {
                sb.Append(Regex.Escape(like[i + 1].ToString()));
                i++;
            }
            else if (ch == wildcard) sb.Append(".*");
            else if (ch == single) sb.Append('.');
            else sb.Append(Regex.Escape(ch));
        }
        sb.Append('$');
        return sb.ToString();
    }

    private static bool EvalIsNull(XmlElement pred, XmlElement feature)
    {
        foreach (XmlNode c in pred.ChildNodes)
            if (c is XmlElement e && e.LocalName == "ValueReference")
            {
                var vals = ResolveValues(e, feature);
                return vals.Count == 0 || vals.All(string.IsNullOrEmpty);
            }
        return false;
    }

    /// <summary>Walks the filter predicate tree and validates every fes:ValueReference
    /// references a known namespace (ddb, gml, or empty). Throws InvalidParameterValue
    /// for unknown namespaces (FES 2.0 §8.3).</summary>
    internal static void ValidateValueReferences(XmlElement pred)
    {
        foreach (XmlNode n in pred.ChildNodes)
        {
            if (n is not XmlElement e) continue;
            if (e.LocalName == "ValueReference" || e.LocalName == "PropertyName")
            {
                var text = e.InnerText?.Trim() ?? "";
                if (text.Length == 0) continue;
                var colon = text.IndexOf(':');
                if (colon <= 0) continue; // unprefixed reference: assume local property
                var prefix = text[..colon];
                var nsUri = e.GetNamespaceOfPrefix(prefix);
                if (string.IsNullOrEmpty(nsUri)) continue;
                if (nsUri == OgcNamespaces.Ddb
                    || nsUri == "http://www.opengis.net/gml/3.2"
                    || nsUri == "http://www.opengis.net/gml")
                    continue;
                throw new OgcException("InvalidParameterValue",
                    $"ValueReference '{text}' refers to unknown namespace '{nsUri}'",
                    400, "ValueReference");
            }
            else
            {
                ValidateValueReferences(e);
            }
        }
    }

    private static (List<string> lefts, List<string> rights) GetOperands(XmlElement pred, XmlElement feature)
    {
        var lefts = new List<string>(); var rights = new List<string>();
        var elems = pred.ChildNodes.OfType<XmlElement>().ToList();
        if (elems.Count < 2) return (lefts, rights);
        // Either Ref op Literal, Literal op Ref, or Literal op Literal.
        foreach (var e in elems)
        {
            if (e.LocalName == "ValueReference")
            {
                if (lefts.Count == 0) lefts.AddRange(ResolveValues(e, feature));
                else rights.AddRange(ResolveValues(e, feature));
            }
            else if (e.LocalName == "Literal")
            {
                if (lefts.Count == 0) lefts.Add(e.InnerText);
                else rights.Add(e.InnerText);
            }
        }
        return (lefts, rights);
    }

    /// <summary>Resolve a fes:ValueReference to one or more string values from the feature.</summary>
    private static List<string> ResolveValues(XmlElement valueRef, XmlElement feature)
    {
        var result = new List<string>();
        var path = valueRef.InnerText.Trim();
        if (string.IsNullOrEmpty(path))
            throw new OgcException("OperationProcessingFailed", "Empty fes:ValueReference", 400, "ValueReference");

        // Simple property names: the last step, without prefix or predicate (FesSqlTranslator)
        var step = path.Split('/').Last();
        var local = FesSqlTranslator.PropertyName(path);

        foreach (XmlNode c in feature.ChildNodes)
            if (c is XmlElement e && e.LocalName == local)
                result.Add(e.InnerText);

        // Special case: rid / gml:id reference.
        if (result.Count == 0 && (local.Equals("id", StringComparison.OrdinalIgnoreCase)
                                  || local.Equals("@gml:id", StringComparison.OrdinalIgnoreCase)
                                  || step.Equals("@gml:id", StringComparison.OrdinalIgnoreCase)))
        {
            var gid = feature.GetAttribute("id", "http://www.opengis.net/gml/3.2");
            if (!string.IsNullOrEmpty(gid)) result.Add(gid);
        }
        return result;
    }
}

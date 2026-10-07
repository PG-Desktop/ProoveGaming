"""Minimal Markdown -> standalone HTML for the macro investigation report.

Handles only what the document actually uses: ATX headings, paragraphs, pipe tables,
fenced code, blockquotes, ordered/unordered lists (incl. nested continuation lines),
horizontal rules, and inline code / bold / italic / links.
"""
import html
import io
import re
import sys


def inline(text):
    """Inline spans. Code first so its contents are never re-processed."""
    placeholders = []

    def stash_code(m):
        placeholders.append(f"<code>{html.escape(m.group(1))}</code>")
        return f"\x00{len(placeholders) - 1}\x00"

    text = re.sub(r"`([^`]+)`", stash_code, text)
    text = html.escape(text)
    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r'<a href="\2">\1</a>', text)
    text = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"(?<![*\w])\*([^*\n]+)\*(?!\w)", r"<em>\1</em>", text)
    for i, rep in enumerate(placeholders):
        text = text.replace(f"\x00{i}\x00", rep)
    return text


def split_row(line):
    return [c.strip() for c in line.strip().strip("|").split("|")]


def convert(md):
    lines = md.split("\n")
    out = []
    i = 0
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()

        # Fenced code
        if stripped.startswith("```"):
            lang = stripped[3:].strip()
            i += 1
            body = []
            while i < len(lines) and not lines[i].strip().startswith("```"):
                body.append(lines[i])
                i += 1
            i += 1
            cls = f' class="lang-{lang}"' if lang else ""
            out.append(f"<pre{cls}><code>{html.escape(chr(10).join(body))}</code></pre>")
            continue

        if not stripped:
            i += 1
            continue

        if re.fullmatch(r"-{3,}", stripped):
            out.append("<hr>")
            i += 1
            continue

        m = re.match(r"(#{1,6})\s+(.*)", stripped)
        if m:
            level = len(m.group(1))
            text = m.group(2)
            anchor = re.sub(r"[^a-z0-9]+", "-", re.sub(r"[`*]", "", text).lower()).strip("-")
            out.append(f'<h{level} id="{anchor}">{inline(text)}</h{level}>')
            i += 1
            continue

        # Table: header row followed by a separator row
        if stripped.startswith("|") and i + 1 < len(lines) and re.fullmatch(
            r"\|[\s:\-|]+\|", lines[i + 1].strip()
        ):
            header = split_row(stripped)
            i += 2
            rows = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append(split_row(lines[i].strip()))
                i += 1
            thead = "".join(f"<th>{inline(c)}</th>" for c in header)
            tbody = "".join(
                "<tr>" + "".join(f"<td>{inline(c)}</td>" for c in r) + "</tr>" for r in rows
            )
            out.append(
                f'<div class="table-scroll"><table><thead><tr>{thead}</tr></thead>'
                f"<tbody>{tbody}</tbody></table></div>"
            )
            continue

        # Blockquote (may span lines and contain its own inline markup)
        if stripped.startswith(">"):
            body = []
            while i < len(lines) and lines[i].strip().startswith(">"):
                body.append(lines[i].strip()[1:].strip())
                i += 1
            joined = " ".join(b for b in body if b)
            out.append(f"<blockquote><p>{inline(joined)}</p></blockquote>")
            continue

        # Lists
        ordered = re.match(r"\d+\.\s+", stripped)
        if ordered or re.match(r"[-*]\s+", stripped):
            tag = "ol" if ordered else "ul"
            items = []
            while i < len(lines):
                cur = lines[i]
                cs = cur.strip()
                if not cs:
                    # A blank line only ends the list if what follows is neither another item of
                    # the same kind nor an indented continuation. Markdown lists are routinely
                    # written with blank lines between items, and treating that as a terminator
                    # restarted the numbering at 1 on every item.
                    nxt = i + 1
                    while nxt < len(lines) and not lines[nxt].strip():
                        nxt += 1
                    if nxt >= len(lines):
                        break
                    follows = lines[nxt]
                    fs = follows.strip()
                    same_kind = (
                        re.match(r"\d+\.\s", fs) if tag == "ol" else re.match(r"[-*]\s", fs)
                    )
                    if follows.startswith(("   ", "\t")) or same_kind:
                        i = nxt
                        continue
                    break
                m_ord = re.match(r"\d+\.\s+(.*)", cs)
                m_ul = re.match(r"[-*]\s+(.*)", cs)
                is_indented = cur.startswith(("   ", "\t"))
                if (m_ord and tag == "ol" and not is_indented) or (
                    m_ul and tag == "ul" and not is_indented
                ):
                    items.append([(m_ord or m_ul).group(1)])
                elif items and (is_indented or not (m_ord or m_ul)):
                    # continuation, or a nested bullet, of the current item
                    items[-1].append(cs)
                else:
                    break
                i += 1
            rendered = []
            for parts in items:
                nested = [p for p in parts[1:] if re.match(r"[-*]\s+", p)]
                flat = [p for p in parts[1:] if not re.match(r"[-*]\s+", p)]
                body = inline(" ".join([parts[0]] + flat))
                if nested:
                    inner = "".join(
                        f"<li>{inline(re.sub(r'^[-*]\\s+', '', n))}</li>" for n in nested
                    )
                    body += f"<ul>{inner}</ul>"
                rendered.append(f"<li>{body}</li>")
            out.append(f"<{tag}>" + "".join(rendered) + f"</{tag}>")
            continue

        # Paragraph
        body = [stripped]
        i += 1
        while i < len(lines) and lines[i].strip() and not re.match(
            r"(#{1,6}\s|```|\||>|-{3,}$|\d+\.\s|[-*]\s)", lines[i].strip()
        ):
            body.append(lines[i].strip())
            i += 1
        out.append(f"<p>{inline(' '.join(body))}</p>")

    return "\n".join(out)


CSS = """
:root{
  --bg:#fbfaf8; --surface:#fff; --text:#22201d; --muted:#6b6660; --rule:#e5e1db;
  --accent:#8a4b2a; --accent-soft:#f5ede7; --code-bg:#f4f1ed; --ok:#2f6b3f; --bad:#a33224;
}
@media (prefers-color-scheme:dark){
  :root{
    --bg:#171614; --surface:#1f1e1b; --text:#e8e4de; --muted:#a09a92; --rule:#332f2b;
    --accent:#e0916a; --accent-soft:#2a2320; --code-bg:#26241f; --ok:#7bbd8c; --bad:#e8877a;
  }
}
:root[data-theme="dark"]{
  --bg:#171614; --surface:#1f1e1b; --text:#e8e4de; --muted:#a09a92; --rule:#332f2b;
  --accent:#e0916a; --accent-soft:#2a2320; --code-bg:#26241f; --ok:#7bbd8c; --bad:#e8877a;
}
:root[data-theme="light"]{
  --bg:#fbfaf8; --surface:#fff; --text:#22201d; --muted:#6b6660; --rule:#e5e1db;
  --accent:#8a4b2a; --accent-soft:#f5ede7; --code-bg:#f4f1ed; --ok:#2f6b3f; --bad:#a33224;
}
*{box-sizing:border-box}
body{
  margin:0; background:var(--bg); color:var(--text);
  font:16px/1.65 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;
  -webkit-font-smoothing:antialiased;
}
.wrap{max-width:56rem;margin:0 auto;padding:3.5rem 1.5rem 6rem}
h1,h2,h3{line-height:1.25;letter-spacing:-.011em}
h1{font-size:2rem;margin:0 0 .4rem;font-weight:650}
h2{
  font-size:1.3rem;margin:3rem 0 .9rem;padding-top:1.4rem;
  border-top:1px solid var(--rule);font-weight:640;
}
h1+p,h1+blockquote{margin-top:.2rem}
h3{font-size:1.05rem;margin:2rem 0 .6rem;font-weight:640;color:var(--accent)}
p{margin:0 0 1rem}
a{color:var(--accent);text-underline-offset:2px}
hr{display:none}
code{
  font-family:ui-monospace,SFMono-Regular,"SF Mono",Menlo,Consolas,monospace;
  font-size:.86em;background:var(--code-bg);padding:.12em .38em;border-radius:4px;
  white-space:nowrap;
}
pre{
  background:var(--code-bg);border:1px solid var(--rule);border-radius:8px;
  padding:1rem 1.1rem;overflow-x:auto;margin:0 0 1.3rem;
}
pre code{background:none;padding:0;font-size:.83rem;line-height:1.55;white-space:pre}
blockquote{
  margin:0 0 1.3rem;padding:.9rem 1.1rem;background:var(--accent-soft);
  border-left:3px solid var(--accent);border-radius:0 6px 6px 0;
}
blockquote p{margin:0;color:var(--text)}
.table-scroll{overflow-x:auto;margin:0 0 1.4rem;border:1px solid var(--rule);border-radius:8px}
table{border-collapse:collapse;width:100%;font-size:.9rem;background:var(--surface)}
th,td{text-align:left;padding:.6rem .8rem;border-bottom:1px solid var(--rule);vertical-align:top}
th{
  font-weight:620;font-size:.78rem;letter-spacing:.04em;text-transform:uppercase;
  color:var(--muted);background:var(--bg);white-space:nowrap;
}
tbody tr:last-child td{border-bottom:none}
td code{white-space:normal}
ul,ol{margin:0 0 1.2rem;padding-left:1.4rem}
li{margin:.35rem 0}
li>ul,li>ol{margin:.35rem 0 .2rem}
.status{
  display:inline-block;margin:.2rem 0 1.1rem;padding:.3rem .7rem;border-radius:999px;
  background:var(--bad);color:#fff;font-size:.78rem;font-weight:640;letter-spacing:.02em;
}
.meta{color:var(--muted);font-size:.88rem;margin:0 0 1.6rem}
@media(max-width:640px){
  .wrap{padding:2.2rem 1.1rem 4rem}
  h1{font-size:1.6rem}
  body{font-size:15px}
}
@media print{
  body{background:#fff}
  .wrap{max-width:none;padding:0}
  h2{break-after:avoid}
  .table-scroll,pre,blockquote{break-inside:avoid}
}
"""


def main(src, dst):
    md = io.open(src, encoding="utf-8").read()
    body = convert(md)

    # The status line and the date line are the first two paragraphs; style them.
    body = body.replace(
        "<p><strong>Status: blocked in firmware, not in this codebase.</strong> Date:",
        '<p class="status-line"><strong>Status: blocked in firmware, not in this codebase.</strong> Date:',
        1,
    )
    body = re.sub(
        r'<p class="status-line"><strong>(Status: [^<]+)</strong>\s*(Date:.*?)</p>',
        r'<div class="status">\1</div>\n<p class="meta">\2</p>',
        body,
        count=1,
        flags=re.S,
    )

    doc = f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Cyrex / Aress macros — investigation result</title>
<style>{CSS}</style>
</head>
<body>
<main class="wrap">
{body}
</main>
</body>
</html>
"""
    io.open(dst, "w", encoding="utf-8", newline="\n").write(doc)
    print(f"wrote {dst} ({len(doc):,} bytes)")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])

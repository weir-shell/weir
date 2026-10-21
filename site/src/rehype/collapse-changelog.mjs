// The changelog page shows the newest release open and every older one
// collapsed [D:changelog]: each later `## <version>` section becomes a
// <details> whose <summary> is the heading itself, so the side TOC's
// anchors and the page search still reach every release. CHANGELOG.md is
// the only file touched; every other page renders as before.
export function collapseChangelog() {
  return (tree, file) => {
    const path = String(file.path ?? file.history?.[0] ?? "");
    if (!path.endsWith("CHANGELOG.md")) return;

    const out = [];
    let section = null;
    let seenFirst = false;

    for (const node of tree.children) {
      const isH2 = node.type === "element" && node.tagName === "h2";
      if (isH2) {
        if (!seenFirst) {
          seenFirst = true;
          out.push(node);
          continue;
        }
        section = {
          type: "element",
          tagName: "details",
          properties: { className: ["changelog-release"] },
          children: [{ type: "element", tagName: "summary", properties: {}, children: [node] }],
        };
        out.push(section);
        continue;
      }
      if (section) section.children.push(node);
      else out.push(node);
    }

    tree.children = out;
  };
}

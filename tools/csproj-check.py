#!/usr/bin/env python3
"""Check that the .csproj lists exactly the files that exist on disk.

This project has repeatedly lost <Page> and <Compile> items, which shows up as
either "The parent file Pages/X.xaml for file Pages/X.xaml.cs cannot be found in
the project file" warnings, or a page that silently does not compile. Both cost a
Windows build round-trip to discover, and both are visible in two directory
walks.

Reports three kinds of drift:
  * a source file on disk that the project does not compile (silently dead code)
  * a XAML page on disk that the project does not include
  * a project entry for a file that no longer exists (stale, usually harmless
    but it breaks the build when the compiler cannot find it)

Exit code 1 when there is drift.
"""

import os
import sys
import xml.etree.ElementTree as ET

NAMESPACE = "{http://schemas.microsoft.com/developer/msbuild/2003}"

# Directories whose .cs files must be compiled by the phone project.
SOURCE_DIRS = ["Core", "Services", "Plugins", "Security", "Transport", "Pages",
               "Properties"]

# Build output and anything that is not part of the phone project.
SKIP_DIRS = {"bin", "obj", "AppPackages", "hosttests", "Assets"}


def norm(path):
    return os.path.normpath(path).replace("\\", "/")


def main(argv):
    root = argv[1] if len(argv) > 1 else "."
    project_dir = os.path.join(root, "KDEConnect.WP81")
    csproj = os.path.join(project_dir, "KDEConnect.WP81.csproj")

    if not os.path.isfile(csproj):
        print("no csproj at %s" % csproj)
        return 2

    tree = ET.parse(csproj)
    compiled, pages, dependent = set(), set(), {}
    applications = set()

    for node in tree.getroot().iter():
        tag = node.tag.replace(NAMESPACE, "")
        include = node.get("Include")
        if not include:
            continue
        target = norm(include)
        if tag == "Compile":
            compiled.add(target)
            for child in node:
                if child.tag.replace(NAMESPACE, "") == "DependentUpon":
                    dependent[target] = (child.text or "").strip()
        elif tag == "Page":
            pages.add(target)
        elif tag == "ApplicationDefinition":
            # App.xaml is an ApplicationDefinition in a WinRT project, not a
            # Page. Expecting it under <Page> is a false positive.
            applications.add(target)

    on_disk_cs, on_disk_xaml = set(), set()
    for dirpath, dirnames, filenames in os.walk(project_dir):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        rel_dir = os.path.relpath(dirpath, project_dir)
        top = rel_dir.split(os.sep)[0]
        if rel_dir == ".":
            top = "."
        for name in filenames:
            rel = norm(os.path.join(rel_dir, name)) if rel_dir != "." else name
            if name.endswith(".xaml"):
                on_disk_xaml.add(rel)
            elif name.endswith(".cs") and top in SOURCE_DIRS + ["."]:
                on_disk_cs.add(rel)

    problems = []

    for rel in sorted(on_disk_cs - compiled):
        problems.append("NOT COMPILED  %s (exists on disk, missing <Compile>)" % rel)
    for rel in sorted(on_disk_xaml - pages - applications):
        problems.append("NOT A PAGE    %s (exists on disk, missing <Page>)" % rel)
    for rel in sorted(compiled - on_disk_cs):
        problems.append("STALE ENTRY   %s (listed in <Compile>, file is gone)" % rel)
    for rel in sorted(pages - on_disk_xaml):
        problems.append("STALE PAGE    %s (listed in <Page>, file is gone)" % rel)

    # Every XAML code-behind should declare its parent, or Visual Studio shows
    # the "parent file cannot be found" warning.
    for rel in sorted(p for p in compiled if p.endswith(".xaml.cs")):
        want = os.path.basename(rel)[: -len(".cs")]
        if dependent.get(rel) != want:
            problems.append("NO PARENT     %s (expected <DependentUpon>%s</DependentUpon>)"
                            % (rel, want))

    print("compiled=%d pages=%d on-disk .cs=%d .xaml=%d"
          % (len(compiled), len(pages), len(on_disk_cs), len(on_disk_xaml)))

    if problems:
        for p in problems:
            print("ERROR " + p)
        print("\n%d problem(s)" % len(problems))
        return 1

    print("0 problems: project file matches the tree")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

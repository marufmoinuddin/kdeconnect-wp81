#!/usr/bin/env python3
"""Static checks for the Windows Phone 8.1 XAML that a C# compiler cannot catch.

Every rule here exists because it already cost a round-trip on a real device.
None of these failures are visible to MSBuild: the build stays green and the app
dies on launch with a XamlParseException, which then has to be recovered from
crash.log.

 1. The file must be well formed XML.
 2. A StaticResource cannot forward-reference a key declared later in the same
    file. WinRT parses resources in document order, so a converter declared
    after the DataTemplate that uses it is "not found" at runtime.
 3. A ThemeResource key that does not exist in the Phone 8.1 resource dictionary
    throws while the page is being constructed, taking the whole page with it.
    Only keys confirmed to render on device are allowed; see
    docs/known-limitations.md L9 and L10.

Usage:
    python3 tools/xaml-check.py [project-root]

Exit code 0 when clean, 1 when any violation is found.
"""

import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

# Keys that have been observed to resolve on a Windows Phone 8.1 device.
# Anything else must be proven before it is added here.
ALLOWED_THEME_KEYS = {
    "ApplicationPageBackgroundThemeBrush",
}

# Members that exist on the desktop/UWP surface and are absent on the phone.
# Recorded in docs/known-limitations.md L10; checked here so they cannot creep
# back into a template.
FORBIDDEN_MEMBERS = [
    "ContentDialog.DefaultButton",
    "DefaultButton=",
    "ContentDialogButton.Primary",
    "ContentDialogButton.Secondary",
    "DownloadsFolder.CreateFileAsync",
    "CoreApplication.RequestRestartAsync",
]


def check_file(path, root_for_report):
    name = os.path.relpath(path, root_for_report)
    errors = []
    warnings = []

    try:
        text = open(path, encoding="utf-8-sig").read()
    except Exception as exc:                       # pragma: no cover
        return ["%s: cannot read (%s)" % (name, exc)], warnings

    try:
        ET.parse(path)
    except Exception as exc:
        errors.append("%s: malformed XML: %s" % (name, exc))
        return errors, warnings

    # --- rule 2: StaticResource must not forward-reference ---------------
    keys = [(m.start(), m.group(1)) for m in re.finditer(r'x:Key="([^"]+)"', text)]
    for m in re.finditer(r'\{StaticResource ([^}]+)\}', text):
        key = m.group(1).strip()
        line = text.count("\n", 0, m.start()) + 1
        defined_before = any(pos < m.start() and k == key for pos, k in keys)
        if not defined_before:
            anywhere = any(k == key for _, k in keys)
            reason = ("declared later in the same file" if anywhere
                      else "not declared in this file")
            errors.append("%s:%d: StaticResource '%s' is %s; WinRT resolves "
                          "resources in document order" % (name, line, key, reason))

    # --- rule 3: ThemeResource keys --------------------------------------
    for m in re.finditer(r'\{ThemeResource ([A-Za-z0-9_]+)\}', text):
        key = m.group(1)
        line = text.count("\n", 0, m.start()) + 1
        if key not in ALLOWED_THEME_KEYS:
            errors.append("%s:%d: ThemeResource '%s' is not in the confirmed "
                          "Phone 8.1 set (see docs/known-limitations.md L9/L10)"
                          % (name, line, key))

    # --- rule 4: members known to be missing on the phone ----------------
    for member in FORBIDDEN_MEMBERS:
        if member in text:
            line = text.count("\n", 0, text.index(member)) + 1
            errors.append("%s:%d: '%s' does not exist on Windows Phone 8.1"
                          % (name, line, member))

    return errors, warnings


def main(argv):
    root = argv[1] if len(argv) > 1 else "."
    project = os.path.join(root, "KDEConnect.WP81")
    if not os.path.isdir(project):
        project = root

    files = sorted(glob.glob(os.path.join(project, "**", "*.xaml"), recursive=True))
    if not files:
        print("no .xaml files found under %s" % project)
        return 1

    total_errors = 0
    for path in files:
        errors, _ = check_file(path, project)
        for e in errors:
            print("ERROR " + e)
        total_errors += len(errors)

    print("\n%d file(s) checked, %d error(s)" % (len(files), total_errors))
    return 0 if total_errors == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))

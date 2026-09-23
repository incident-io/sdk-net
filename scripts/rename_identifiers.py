"""PascalCase the snake_case names Kiota takes from URL path segments.

Usage: rename_identifiers.py <generated-dir>

Kiota turns each path segment into a namespace, a request builder class and a
property, keeping the segment's spelling: /v2/alert_routes becomes
client.V2.Alert_routes and Alert_routesRequestBuilder. This rewrites those to
AlertRoutes and AlertRoutesRequestBuilder, and renames the matching folders and
files.

Only identifiers that start with a capital letter are touched. The URL
templates Kiota builds requests from ("{+baseurl}/v2/alert_routes") and the
query parameter names ([QueryParameter("sort_by")]) are lowercase, so nothing
that goes over the wire changes. All-caps preprocessor symbols such as
NETSTANDARD2_1_OR_GREATER are left alone.

Fails if two identifiers would get the same name, or if the count of renamed
identifiers falls below a floor, so a generator change that alters the naming
fails the release rather than quietly changing the public API.
"""

import os
import re
import sys

# Set a little under the count at the time of writing.
RENAMED_FLOOR = 150

IDENTIFIER = re.compile(r"\b[A-Z][A-Za-z0-9]*_[A-Za-z0-9_]*\b")
PREPROCESSOR_SYMBOL = re.compile(r"^[A-Z0-9_]+$")
SNAKE_NAME = re.compile(r"[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+")


def pascal(name):
    return "".join(p[:1].upper() + p[1:] for p in name.split("_") if p)


def source_files(root):
    for directory, _, files in os.walk(root):
        for name in files:
            if name.endswith(".cs"):
                yield os.path.join(directory, name)


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    root = sys.argv[1]

    sources = {path: open(path).read() for path in source_files(root)}

    renames = {
        name: pascal(name)
        for text in sources.values()
        for name in IDENTIFIER.findall(text)
        if not PREPROCESSOR_SYMBOL.match(name)
    }

    targets = {}
    for old, new in renames.items():
        targets.setdefault(new, []).append(old)
    clashes = {new: olds for new, olds in targets.items() if len(olds) > 1}
    if clashes:
        sys.exit(f"rename_identifiers: these would get the same name: {clashes}")

    for path, text in sources.items():
        renamed = IDENTIFIER.sub(lambda m: renames.get(m.group(0), m.group(0)), text)
        if renamed != text:
            with open(path, "w") as f:
                f.write(renamed)

    # Bottom-up, so a directory is renamed after everything inside it.
    for directory, dirs, files in os.walk(root, topdown=False):
        for name in files + dirs:
            new_name = SNAKE_NAME.sub(lambda m: pascal(m.group(0)), name)
            if new_name != name:
                os.rename(os.path.join(directory, name), os.path.join(directory, new_name))

    print(f"rename_identifiers: renamed {len(renames)} identifiers")
    if len(renames) < RENAMED_FLOOR:
        sys.exit(
            f"rename_identifiers: {len(renames)} is below the floor of {RENAMED_FLOOR}; "
            "check whether Kiota changed how it names path segments."
        )


if __name__ == "__main__":
    main()

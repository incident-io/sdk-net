"""Reshape the published schema before Kiota sees it.

Usage: prepare_spec.py <openapi.json> <out.json>

openapi.json stays exactly as fetched: it is what oasdiff compares, and what a
reader can check against https://api.incident.io/v1/openapiV3.json. This writes
a second copy for the generator only. Every change here is to names and doc
text, or removes something the generated client cannot send correctly. None of
it changes what goes over the wire.

Kiota has no naming options, so the schema is the main place to steer its
output. The one thing it can't reach is the names Kiota takes from URL path
segments, since the URL is built from the same text; scripts/rename_identifiers.py
handles those after generation.

Every pass counts what it did and fails the run when a count falls below a
floor, so an upstream schema or generator change surfaces as a failed release
rather than a silently different public API.
"""

import json
import re
import sys

# Floors, set a little under the counts at the time of writing. A pass that
# suddenly does much less means the schema changed shape under it.
EXTRACTED_FLOOR = 250
NAMED_ENUM_FLOOR = 250
DEPRECATED_FLOOR = 30

DEPRECATION_MESSAGE = (
    "This endpoint is deprecated in the incident.io API. "
    "See https://api-docs.incident.io/ for the recommended replacement."
)

HTTP_METHODS = {"get", "put", "post", "delete", "patch", "head", "options", "trace"}


def pascal(value):
    """splunk_on_call -> SplunkOnCall, de-DE -> DeDE, N/A -> NA."""
    return "".join(p[:1].upper() + p[1:] for p in re.split(r"[^A-Za-z0-9]+", value) if p)


def operations(spec):
    for path, item in spec["paths"].items():
        for method, op in item.items():
            if method in HTTP_METHODS:
                yield path, method, op


def strip_object_query_params(spec):
    """Remove query parameters whose schema is an object.

    These are the list filters, such as created_at[gte]=2025-01-01. Kiota has
    no support for object or deepObject query parameters: it types them as a
    plain string and sends `created_at=<value>`, which the server does not read
    as a filter. Leaving them in would put a property on every list request
    that silently does nothing. The README shows how to send filters with
    WithUrl instead.
    """
    removed = 0
    for _, _, op in operations(spec):
        params = op.get("parameters")
        if not params:
            continue
        kept = [
            p
            for p in params
            if not (p.get("in") == "query" and p.get("schema", {}).get("type") == "object")
        ]
        removed += len(params) - len(kept)
        op["parameters"] = kept
    return removed


def mark_deprecations(spec):
    """Give every deprecated operation a message for its [Obsolete] attribute.

    Kiota already emits [Obsolete] for `deprecated: true`, but with an empty
    message, so a caller sees `is obsolete: ''`. It reads the text from the
    x-ms-deprecation extension. Only `description` is set: Kiota appends the
    date and version fields to it with awkward punctuation.
    """
    marked = 0
    for _, _, op in operations(spec):
        if op.get("deprecated"):
            op["x-ms-deprecation"] = {"description": DEPRECATION_MESSAGE}
            marked += 1
    return marked


def camel_path_params(spec):
    """Rename snake_case path parameters to camelCase.

    Kiota names the request builder for /v2/heartbeat/{alert_source_config_id}
    WithAlert_source_config_ItemRequestBuilder. The parameter name is only a
    placeholder, filled in by position, so renaming it changes nothing on the
    wire. The path segments themselves can't be renamed here, because Kiota
    builds the URL from them; scripts/rename_identifiers.py renames them in the
    output.
    """
    renamed = set()

    def camel(name):
        return name[:1] + pascal(name)[1:]

    paths = {}
    for path, item in spec["paths"].items():
        new_path = re.sub(r"\{(\w+)\}", lambda m: "{" + camel(m.group(1)) + "}", path)
        for owner in [item] + [op for m, op in item.items() if m in HTTP_METHODS]:
            for param in owner.get("parameters") or []:
                if param.get("in") == "path" and "_" in param["name"]:
                    renamed.add(param["name"])
                    param["name"] = camel(param["name"])
        paths[new_path] = item
    spec["paths"] = paths
    return len(renamed)


def needs_own_type(schema):
    """Whether Kiota would generate a class or enum for this inline schema."""
    if not isinstance(schema, dict) or "$ref" in schema:
        return False
    return "enum" in schema or schema.get("type") == "object" or "properties" in schema


def extract_inline_schemas(spec):
    """Move inline enums and objects under components/schemas.

    Kiota names an inline schema after its parent and the property, joined by
    an underscore and keeping the property's snake_case:
    UsersShowPagingProviderResultV2_preferred_escalation_provider. As a named
    component it takes the component's name instead, so this gives each one
    the parent's name plus the property in PascalCase:
    UsersShowPagingProviderResultV2PreferredEscalationProvider.

    Component names never go over the wire, so this changes only C# type
    names. The name depends on nothing but the parent and the property, so it
    stays the same across schema changes: deduplicating identical enums would
    read nicer, but a second schema gaining the same enum would then rename
    the first one's type, which is a breaking change.
    """
    schemas = spec["components"]["schemas"]
    extracted = 0

    def add(name, schema, owner):
        if name in schemas:
            sys.exit(
                f"prepare_spec: {owner} would become {name}, which already exists. "
                "Pick a different naming rule for this case."
            )
        schemas[name] = schema

    # Query parameters have no parent schema, and Kiota names theirs
    # Get<param>QueryParameterType, again keeping snake_case
    # (GetSort_byQueryParameterType). They take the operationId instead:
    # "Incidents V2#List" and sort_by make IncidentsV2ListSortBy.
    for _, _, op in operations(spec):
        for param in op.get("parameters") or []:
            if param.get("in") == "query" and needs_own_type(param.get("schema")):
                name = pascal(op["operationId"]) + pascal(param["name"])
                add(name, param["schema"], f"{op['operationId']} {param['name']}")
                param["schema"] = {"$ref": f"#/components/schemas/{name}"}
                extracted += 1

    queue = list(schemas.items())
    while queue:
        parent, schema = queue.pop()
        for prop, prop_schema in (schema.get("properties") or {}).items():
            name = parent + pascal(prop)
            if needs_own_type(prop_schema):
                target = prop_schema
                schema["properties"][prop] = {"$ref": f"#/components/schemas/{name}"}
            elif prop_schema.get("type") == "array" and needs_own_type(prop_schema.get("items")):
                target = prop_schema["items"]
                prop_schema["items"] = {"$ref": f"#/components/schemas/{name}"}
            else:
                continue
            add(name, target, f"{parent}.{prop}")
            queue.append((name, target))
            extracted += 1
    return extracted


def name_enum_members(spec):
    """PascalCase enum member names, keeping the wire values.

    Kiota capitalises the first letter and keeps the rest, so splunk_on_call
    becomes Splunk_on_call. It reads member names from x-ms-enum when present.
    An enum where any value would not make a valid, unique C# identifier (an
    empty string, a leading digit) is left to Kiota's defaults, which already
    handle those (1 becomes One).
    """
    named = skipped = 0

    def walk(node):
        nonlocal named, skipped
        if isinstance(node, dict):
            values = node.get("enum")
            if node.get("type") == "string" and isinstance(values, list):
                names = [pascal(v) for v in values]
                valid = all(re.fullmatch(r"[A-Za-z][A-Za-z0-9]*", n) for n in names)
                if valid and len(set(names)) == len(names):
                    node["x-ms-enum"] = {
                        "modelAsString": False,
                        "values": [{"value": v, "name": n} for v, n in zip(values, names)],
                    }
                    named += 1
                else:
                    skipped += 1
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(spec)
    return named, skipped


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    src, dst = sys.argv[1:]
    with open(src) as f:
        spec = json.load(f)

    removed = strip_object_query_params(spec)
    deprecated = mark_deprecations(spec)
    path_params = camel_path_params(spec)
    extracted = extract_inline_schemas(spec)
    named, skipped = name_enum_members(spec)

    print(f"prepare_spec: removed {removed} object query parameters")
    print(f"prepare_spec: marked {deprecated} deprecated operations")
    print(f"prepare_spec: renamed {path_params} path parameters")
    print(f"prepare_spec: extracted {extracted} inline schemas")
    print(f"prepare_spec: named members of {named} enums, left {skipped} to Kiota")

    failures = [
        f"{what}: {got} is below the floor of {floor}"
        for what, got, floor in [
            ("extracted inline schemas", extracted, EXTRACTED_FLOOR),
            ("named enums", named, NAMED_ENUM_FLOOR),
            ("deprecated operations", deprecated, DEPRECATED_FLOOR),
        ]
        if got < floor
    ]
    if failures:
        for failure in failures:
            print(f"prepare_spec: {failure}", file=sys.stderr)
        sys.exit("prepare_spec: the schema changed shape; check the passes above still apply.")

    with open(dst, "w") as f:
        json.dump(spec, f, indent=1)
        f.write("\n")


if __name__ == "__main__":
    main()

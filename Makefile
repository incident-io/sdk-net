# The Kiota version lives in .config/dotnet-tools.json, pinned deliberately:
# Kiota reshapes its output in minor releases, which would reshape our public
# API. Bumping it is a human-reads-the-diff operation, never automatic.
#
# The .NET SDK is pinned in global.json for the same reason the generator is: a
# new SDK brings new analyzers, and verify builds with -warnaserror, so an
# unpinned SDK would halt the hourly release on the day it ships.

# Pinned: oasdiff decides whether a release is a major. The release workflow
# reads this line rather than keeping a second copy.
OASDIFF_VERSION := 1.32.1
# Versioned: an unversioned path would keep serving a stale binary after
# OASDIFF_VERSION is bumped.
OASDIFF         := /tmp/oasdiff-$(OASDIFF_VERSION)
# oasdiff ships one universal darwin build and per-arch linux builds.
OASDIFF_OS       := $(shell uname -s | tr 'A-Z' 'a-z')
OASDIFF_PLATFORM := $(if $(filter darwin,$(OASDIFF_OS)),darwin_all,$(OASDIFF_OS)_$(shell uname -m | sed 's/x86_64/amd64/;s/aarch64/arm64/'))

SCHEMA_URL := https://api.incident.io/v1/openapiV3.json
PROJECT    := src/IncidentIo/IncidentIo.csproj
TESTS      := tests/IncidentIo.Tests/IncidentIo.Tests.csproj
GENERATED  := src/IncidentIo/Generated
PREPARED   := build/openapi.prepared.json

# The release workflow passes the version it is about to publish, so the
# package verify builds is the package it ships.
VERSION ?= 0.0.0-dev

# Every build, test and pack passes the same properties. Different global
# properties are a different build to MSBuild, so a test or pack with fewer of
# them would recompile all ~1350 generated files for both target frameworks.
BUILD_PROPS := -c Release -p:Version=$(VERSION) -p:ContinuousIntegrationBuild=true

# Without this a failed download leaves a partial $(OASDIFF) behind, which make
# then treats as up to date on every later run.
.DELETE_ON_ERROR:

.DEFAULT_GOAL := help
.PHONY: help fetch generate verify test api-compat oasdiff clean

help:
	@grep -E '^[a-z-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-12s\033[0m %s\n", $$1, $$2}'

# -L because without it a redirect is a silent success writing zero bytes,
# which parses as an empty schema. OUT lets the release workflow fetch to a
# scratch path so it still has the previous schema to diff against.
OUT ?= openapi.json

fetch: ## Fetch the live schema (OUT= to write elsewhere)
	curl -sfSL $(SCHEMA_URL) -o $(OUT)

generate: openapi.json ## Regenerate the client from the committed schema
	dotnet tool restore
	@mkdir -p build
	python3 scripts/prepare_spec.py openapi.json $(PREPARED)
	# --clean-output empties $(GENERATED) first, so an endpoint or model removed
	# upstream doesn't leave a stale file behind. It is its own directory for
	# that reason: the hand-written client code sits next to it.
	#
	# --exclude-backward-compatible drops the [Obsolete] shims Kiota keeps for
	# its own older output. We have no older output, and they would double the
	# surface every consumer sees.
	dotnet kiota generate \
		--language CSharp \
		--openapi $(PREPARED) \
		--class-name IncidentIoClient \
		--namespace-name IncidentIo \
		--output $(GENERATED) \
		--exclude-backward-compatible \
		--clean-output \
		--log-level Warning
	# Kiota keeps path segments' snake_case in C# names (V2.Alert_routes).
	python3 scripts/rename_identifiers.py $(GENERATED)

verify: ## Build with warnings as errors, and pack
	# -warnaserror over machine-written code is only safe because the SDK and
	# Kiota are both pinned; see the top of this file.
	dotnet build $(PROJECT) $(BUILD_PROPS) -warnaserror
	# Pack runs package validation, which checks both target frameworks expose
	# the same API.
	rm -rf artifacts
	dotnet pack $(PROJECT) $(BUILD_PROPS) --no-build -o artifacts

test: verify ## Everything verify does, plus the tests on every target framework
	dotnet test --project $(TESTS) $(BUILD_PROPS)

# oasdiff compares the schema; this compares the C# API a consumer compiles
# against. They catch disjoint things: a renamed component schema or a
# retagged operation leaves the wire contract alone and renames a type.
#
# Before the first release there is nothing on nuget.org to compare against,
# so this reports that and passes.
#
# A reported break makes the release a major. To accept one as a minor, run
# it with
# API_COMPAT_FLAGS=-p:ApiCompatGenerateSuppressionFile=true, which writes
# src/IncidentIo/CompatibilitySuppressions.xml. See CONTRIBUTING.md.
API_COMPAT_FLAGS ?=

api-compat: verify ## Compare the public API against the last published package
	@status="$$(curl -s -o /tmp/nuget-versions.json -w '%{http_code}' \
		https://api.nuget.org/v3-flatcontainer/incidentio/index.json)"; \
	if [ "$$status" = "404" ]; then \
		echo "IncidentIo is not on nuget.org yet, so there is no baseline."; \
		exit 0; \
	elif [ "$$status" != "200" ]; then \
		echo "nuget.org returned $$status; cannot tell whether a baseline exists."; \
		exit 1; \
	fi; \
	baseline="$$(python3 -c "import json; print([v for v in json.load(open('/tmp/nuget-versions.json'))['versions'] if '-' not in v][-1])")"; \
	echo "Comparing against IncidentIo $$baseline"; \
	dotnet pack $(PROJECT) $(BUILD_PROPS) --no-build -p:PackageValidationBaselineVersion=$$baseline $(API_COMPAT_FLAGS) -o /tmp/api-compat

# The same gate the release runs, runnable by hand.
oasdiff: $(OASDIFF) ## Diff the live schema against the committed one, as the release does
	@$(MAKE) --no-print-directory fetch OUT=/tmp/openapi.json.new
	# The same sanity check the release does before trusting the bytes. curl -f
	# passes a 200 with an empty or truncated body, and oasdiff reads an empty
	# file as every path having been removed — a full "everything is breaking"
	# report over a bad proxy response.
	@python3 -c "import json; d=json.load(open('/tmp/openapi.json.new')); n=len(d.get('paths') or {}); \
		exit(0) if n >= 100 else exit(f'only {n} paths in the fetched schema')"
	$(OASDIFF) breaking openapi.json /tmp/openapi.json.new \
		--severity-levels oasdiff-severity.txt --fail-on ERR

$(OASDIFF):
	curl -sfSL "https://github.com/oasdiff/oasdiff/releases/download/v$(OASDIFF_VERSION)/oasdiff_$(OASDIFF_VERSION)_$(OASDIFF_PLATFORM).tar.gz" \
		| tar -xzO oasdiff > $@
	chmod +x $@

clean: ## Remove build output
	rm -rf build artifacts src/IncidentIo/bin src/IncidentIo/obj tests/IncidentIo.Tests/bin tests/IncidentIo.Tests/obj

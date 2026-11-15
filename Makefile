# Convenience targets. Requires .NET 10 SDK and jprm (pip install jprm).
VERSION ?= $(shell grep -Po '^version: *"?\K[^"]+' build.yaml)

.PHONY: test build package clean

test: ## run unit tests
	dotnet test Jellyfin.Plugin.OuterFiles.slnx

build: ## build the plugin
	dotnet build Jellyfin.Plugin.OuterFiles.slnx -c Release

package: ## build the plugin zip into ./artifacts
	mkdir -p artifacts
	jprm plugin build . --dotnet-framework net10.0 --version $(VERSION) --output "$$PWD/artifacts"

clean:
	rm -rf artifacts bin obj */bin */obj tests/*/bin tests/*/obj

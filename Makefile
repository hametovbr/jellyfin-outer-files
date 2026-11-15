# Convenience targets. Requires .NET 10 SDK and jprm (pip install jprm).
VERSION ?= 0.1.0.0

.PHONY: test build package check clean

test: ## run unit tests
	dotnet test tests/Jellyfin.Plugin.OuterFiles.Tests/Jellyfin.Plugin.OuterFiles.Tests.csproj

build: ## build the plugin
	dotnet build Jellyfin.Plugin.OuterFiles/Jellyfin.Plugin.OuterFiles.csproj -c Release

package: ## build the plugin zip into ./artifacts
	mkdir -p artifacts
	jprm plugin build . --dotnet-framework net10.0 --version $(VERSION) --output "$$PWD/artifacts"

check: ## validate project layout with the jellyfin-plugin-development skill checker
	python3 "$$HOME/.dsh/skills/jellyfin-plugin-development/scripts/check_plugin.py" .

clean:
	rm -rf artifacts bin obj */bin */obj tests/*/bin tests/*/obj

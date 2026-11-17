# SteamWorkshopAgent local build configuration

SLN ?= SteamWorkshopAgent.slnx
PROJECT ?= src/SteamWorkshopAgent/SteamWorkshopAgent.csproj
COMPANION_PROJECT ?= src/SteamWorkshopAgent.BridgeTools/SteamWorkshopAgent.BridgeTools.csproj
COMPANION_OUTPUT ?= artifacts/BridgeTools/SteamWorkshopAgent/SteamWorkshopAgent.BridgeTools.dll
INSTALL_DIR ?= $(HOME)/.codex/mcp-servers/steam-workshop-agent
BRIDGE_TOOLS_DIR ?= $(HOME)/Library/Application Support/Steam/steamapps/common/RimWorld/BridgeTools/SteamWorkshopAgent
BINARY_NAME ?= SteamWorkshopAgent
RID ?= osx-arm64

.PHONY: all
all: build

.PHONY: build
build:
	@python3 scripts/check.py build "$(SLN)"

.PHONY: test
test:
	@python3 scripts/check.py test "$(SLN)"

.PHONY: install
install:
	mkdir -p "$(INSTALL_DIR)"
	dotnet publish $(PROJECT) -c Release -r $(RID) --self-contained true \
		-p:PublishSingleFile=true \
		-p:PublishTrimmed=false \
		-o "$(INSTALL_DIR)"
	chmod +x "$(INSTALL_DIR)/$(BINARY_NAME)"
	mkdir -p "$(BRIDGE_TOOLS_DIR)"
	dotnet build $(COMPANION_PROJECT) -c Release --nologo -v:minimal
	cp "$(COMPANION_OUTPUT)" "$(BRIDGE_TOOLS_DIR)/SteamWorkshopAgent.BridgeTools.dll"

.PHONY: clean
clean:
	rm -rf src/SteamWorkshopAgent/bin src/SteamWorkshopAgent/obj
	rm -rf tests/SteamWorkshopAgent.Tests/bin tests/SteamWorkshopAgent.Tests/obj
	rm -rf src/SteamWorkshopAgent.BridgeTools/bin src/SteamWorkshopAgent.BridgeTools/obj
	rm -rf artifacts/BridgeTools/SteamWorkshopAgent

.PHONY: help
help:
	@echo "Available targets:"
	@echo "  build   - Build the server"
	@echo "  test    - Run tests"
	@echo "  install - Publish MCP binary and the RimBridge companion DLL"
	@echo "  clean   - Remove build artifacts"
	@echo "  help    - Show this help"

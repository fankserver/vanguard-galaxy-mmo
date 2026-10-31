DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)
SERVER := server/VGMMO.Server/VGMMO.Server.csproj
CLIENT := client/VGMMO.Client/VGMMO.Client.csproj
GAME_DIR := /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy

.PHONY: build run selftest client-build client-deploy clean

build:
	$(DOTNET) build $(SERVER) -c Release

run: build
	cd server && $(DOTNET) run --project VGMMO.Server -c Release --no-build

selftest: build
	$(DOTNET) run --project $(SERVER) -c Release --no-build -- --self-test

client-build:
	$(DOTNET) build $(CLIENT) -c Release

# Deploys only the plugin + its two dependencies (never the System.* shims or
# Newtonsoft — the game ships its own). Folder-based like VGTTS.
client-deploy: client-build
	mkdir -p "$(GAME_DIR)/BepInEx/plugins/VGMMO"
	cp client/VGMMO.Client/bin/Release/netstandard2.1/VGMMO.Client.dll "$(GAME_DIR)/BepInEx/plugins/VGMMO/"
	cp client/VGMMO.Client/bin/Release/netstandard2.1/VGMMO.Protocol.dll "$(GAME_DIR)/BepInEx/plugins/VGMMO/"
	cp client/VGMMO.Client/bin/Release/netstandard2.1/LiteNetLib.dll "$(GAME_DIR)/BepInEx/plugins/VGMMO/"
	# The game does NOT ship Newtonsoft (verified on the current build); deployed plugins
	# carry a real copy. Source it from the NuGet cache (matching the compile reference).
	cp "$(HOME)/.nuget/packages/newtonsoft.json/13.0.3/lib/netstandard2.0/Newtonsoft.Json.dll" "$(GAME_DIR)/BepInEx/plugins/VGMMO/"

clean:
	rm -rf server/VGMMO.Server/bin server/VGMMO.Server/obj server/VGMMO.Protocol/bin server/VGMMO.Protocol/obj server/data client/VGMMO.Client/bin client/VGMMO.Client/obj

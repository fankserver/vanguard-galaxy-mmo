DOTNET ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)
SERVER := server/VGMMO.Server/VGMMO.Server.csproj

.PHONY: build run selftest clean

build:
	$(DOTNET) build $(SERVER) -c Release

run: build
	cd server && $(DOTNET) run --project VGMMO.Server -c Release --no-build

selftest: build
	$(DOTNET) run --project $(SERVER) -c Release --no-build -- --self-test

clean:
	rm -rf server/VGMMO.Server/bin server/VGMMO.Server/obj server/VGMMO.Protocol/bin server/VGMMO.Protocol/obj server/data

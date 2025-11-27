# SnapCD Runner

The SnapCD Runner is a worker service that executes Terraform/OpenTofu commands and reports status back to the SnapCD server.

## Prerequisites

- .NET 10.0 SDK
- Docker (for container builds)



## Configuration

The runner is configured via `SnapCd.Runner/appsettings.json` which by default looks like this:

```json
{
  "Runner": {
    "Id": "TODO",
    "Instance": "TODO",
    "Credentials": {
      "ClientId": "TODO",
      "ClientSecret": "TODO"
    },
    "OrganizationId": "TODO"
  },
  "Server": {
    "Url": "https://snapcd.io"
  },
  "WorkingDirectory": {
    "WorkingDirectory": "~/.snapcd/runner",
    "TempDirectory": "~/.snapcd/runner/.temp"
  },
  "HooksPreapproval": {
    "Enabled": false,
    "PreapprovedHooksDirectory": "~/.snapcd/preapproved-hooks"
  }
}
```

In development (i.e starting the runner in the debugger) these settings can be overridden by creating a local "SnapCd.Runner/appsettings.Development.json". For example, to override the "Runner" section with values that allows you to actually interact with snapcd.io:

`SnapCd.Runner/appsettings.Development.json`
```json
{
  "Runner": {
    "Id": "90000000-0000-0000-0000-000000000000",
    "Instance": "myrunner",
    "Credentials": {
      "ClientId": "someClientId",
      "ClientSecret": "someClientSecret"
    },
    "OrganizationId": "99900000-0000-0000-0000-000000000000"
  }
}
```

The same principal also applies in production. For that you can create an `appsettings.Production.json` file. See "Building locally" below.

You can override the ennvironment with:
```bash
# Development (default when debugging)
export ASPNETCORE_ENVIRONMENT=Development
```

```bash
# Production
export ASPNETCORE_ENVIRONMENT=Production
```

Every configuration value can also be overridden using environment values, e.g.:

```bash
export Runner__Credentials__ClientId=someOtherClientId
```


## Building Locally

To build the runner as a self-contained single-file executable:

```bash
ARCH=linux-x64

# Linux x64
dotnet publish SnapCd.Runner/SnapCd.Runner.csproj \
  -c Release \
  -r $ARCH \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o artifacts
```

Along with the binary, in the "artifacts" directory you will find the "appsettings.json" and "appsettings.Production.json" (if defined) that is present in the "SnapCd.Runner" directory. 

```bash
cd artifacts 
./SnapCd.Runner
```

## Building Docker Images

The Dockerfiles expect a pre-built binary in the `artifacts/` directory. The `Dockerfile` contains only the pre-requisites to be able to runner SnapCd.Runner. `Dockerfile.azure` additionally installs the Azure CLI.

```bash
docker build -t snapcd-runner:latest -f Dockerfile .
```

### Running Docker Containers

```bash
# Run with default settings (this will likely crash unless you've modified appsettings.json)
docker run --rm --name snapcd-runner snapcd-runner:latest

# Run with mounted configuration (set your production overrides in your `/path/to/appsettings.Production.json`)
docker run --rm --name snapcd-runner \
  -v /path/to/appsettings.Production.json:/app/appsettings.Production.json:ro \
  snapcd-runner:latest
  
# Run with ClientSecret defined as env var
docker run --rm --name snapcd-runner \
  -e Runner__Credentials__ClientSecret=someSecret \
  snapcd-runner:latest



# Run with persistent working directory
docker run --rm --name snapcd-runner \
  -v snapcd-runner-data:/data/.snapcd \
  -e WorkingDirectory__WorkingDirectory=/data/.snapcd/runner \
  -e WorkingDirectory__TempDirectory=/data/.snapcd/runner/.temp \
  snapcd-runner:latest

# Run with pre-approved hooks (any scripts defined locally in /path/to/preapproved-hooks will be pre-approved on the runner)
docker run --rm --name snapcd-runner \
  -v /path/to/preapproved-hooks:/app/preapproved-hooks:ro \
  -e HooksPreapproval__Enabled=true \
  -e HooksPreapproval__PreapprovedHooksDirectory=/app/preapproved-hooks \
  snapcd-runner:latest
```


## Running Tests

```bash
dotnet test
```
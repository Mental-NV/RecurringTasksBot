#!/usr/bin/env python3
"""Check the compiled ARM dependency and secrecy contract without calling Azure."""
import json
import sys
from pathlib import Path


def entries(template):
    resources = template["resources"]
    return list(resources.items()) if isinstance(resources, dict) else [
        (resource["name"], resource) for resource in resources
    ]


template = json.loads(Path(sys.argv[1]).read_text())
resources = entries(template)
module_key, module = next((key, value) for key, value in resources
                          if value["type"] == "Microsoft.Resources/deployments" and value["name"] == "monitoring")
app = next(value for _, value in resources if value["type"] == "Microsoft.Web/sites")
settings = {item["name"]: item["value"] for item in app["properties"]["siteConfig"]["appSettings"]}
connection = settings["APPLICATIONINSIGHTS_CONNECTION_STRING"]

# A direct reference() to an existing component can run before its creating
# module. Secure module outputs must be consumed after that deployment completes.
assert "listOutputsWithSecureValues(" in connection and "applicationInsightsConnectionString" in connection, \
    "Function App must consume the secure monitoring output, not read an existing component directly"
assert any(dependency == module_key or "'monitoring'" in dependency for dependency in app.get("dependsOn", [])), \
    "Function App must depend on the monitoring deployment"
output = module["properties"]["template"]["outputs"]["applicationInsightsConnectionString"]
assert output["type"].lower() == "securestring", "Telemetry connection string must be a secure module output"
assert "ConnectionString" not in json.dumps(template.get("outputs", {})), \
    "Parent deployment must not expose the telemetry connection string"
assert settings["SCALE_CONTROLLER_LOGGING_ENABLED"] == "AppInsights:None", \
    "Verbose scale-controller logging must remain temporary"

print("PASS: compiled template waits for the monitoring module and keeps its connection-string output secure")

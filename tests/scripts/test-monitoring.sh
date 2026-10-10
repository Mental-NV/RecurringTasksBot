#!/usr/bin/env bash
# Exercise provider preflight and cap safeguards against a CLI shim, never Azure.
set -euo pipefail
cd "$(dirname "$0")/../.."
python3 - <<'PY'
import json, os, subprocess, tempfile
from pathlib import Path

with tempfile.TemporaryDirectory(prefix="recurringtasks-monitoring-test-") as directory:
    root = Path(directory)
    params = json.loads(Path("infra/main.parameters.json").read_text())
    params["parameters"]["monitoringWorkspaceName"]["value"] = "law-fixture"
    params["parameters"]["applicationInsightsName"]["value"] = "appi-fixture"
    params["parameters"]["monitoringDailyCapGb"]["value"] = "0.1"
    params["parameters"]["storageConnectionString"]["value"] = "DO_NOT_LOG_FIXTURE_SECRET"
    parameters = root / "parameters.json"
    parameters.write_text(json.dumps(params))
    calls = root / "calls.jsonl"
    shim = root / "az"
    shim.write_text('''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
with open(os.environ["MONITORING_TEST_CALLS"], "a") as log:
    log.write(json.dumps(args) + "\\n")
def option(name):
    return args[args.index(name) + 1]
if args[:2] == ["provider", "show"]:
    assert option("--query") == "registrationState"
    assert option("--output") == "tsv"
    namespace = option("--namespace")
    if os.environ.get("MONITORING_TEST_FAIL") == namespace:
        sys.exit(42)
    states = json.loads(os.environ.get("MONITORING_TEST_PROVIDER_STATES", '{}'))
    print(states.get(namespace, "Registered"))
elif args[:2] == ["extension", "add"]:
    assert option("--name") == "application-insights"
elif args[:5] == ["monitor", "app-insights", "component", "billing", "update"]:
    assert option("--app") == "appi-fixture"
    assert option("--resource-group") == "rg-fixture"
    assert option("--cap") == "0.1"
    assert option("--stop") == "false"
    assert option("--output") == "none"
    if os.environ.get("MONITORING_TEST_FAIL") == "update":
        sys.exit(42)
elif args[:4] == ["monitor", "log-analytics", "workspace", "show"]:
    assert option("--workspace-name") == "law-fixture"
    assert option("--resource-group") == "rg-fixture"
    assert option("--query") == "workspaceCapping.dailyQuotaGb"
    if os.environ.get("MONITORING_TEST_FAIL") == "workspace-show":
        sys.exit(42)
    print(os.environ.get("MONITORING_TEST_WORKSPACE_CAP", "0.1"))
elif args[:5] == ["monitor", "app-insights", "component", "billing", "show"]:
    assert option("--app") == "appi-fixture"
    assert option("--resource-group") == "rg-fixture"
    print(os.environ.get("MONITORING_TEST_BILLING", '{"dataVolumeCap":{"cap":0.1}}'))
else:
    raise SystemExit("Unexpected Azure CLI operation: " + repr(args))
''')
    shim.chmod(0o755)
    base_env = {**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                "MONITORING_TEST_CALLS": str(calls)}
    for key in ("MONITORING_TEST_FAIL", "MONITORING_TEST_WORKSPACE_CAP", "MONITORING_TEST_BILLING",
                "MONITORING_TEST_PROVIDER_STATES"):
        base_env.pop(key, None)

    def run(*, changes=None, options=None, success=True, cli_calls=None):
        calls.write_text("")
        command = ["bash", "scripts/configure-monitoring.sh"] + (options if options is not None else
                   ["--resource-group", "rg-fixture", "--parameters", str(parameters)])
        result = subprocess.run(command, env={**base_env, **(changes or {})}, text=True, capture_output=True)
        assert (result.returncode == 0) == success, (command, result.returncode, result.stdout, result.stderr)
        assert "DO_NOT_LOG_FIXTURE_SECRET" not in result.stdout + result.stderr
        records = [json.loads(line) for line in calls.read_text().splitlines()]
        if cli_calls is not None:
            assert len(records) == cli_calls, records
        return records

    # Repeated runs use only the existing component's billing update and reads.
    assert run(cli_calls=4) == run(cli_calls=4)
    run(changes={"MONITORING_TEST_BILLING": '{"DataVolumeCap":{"Cap":0.1}}'}, cli_calls=4)
    for value in ("-1", "0.2", "", "null", "NaN"):
        run(changes={"MONITORING_TEST_WORKSPACE_CAP": value}, success=False, cli_calls=4)
    for value in ('{"dataVolumeCap":{"cap":0.2}}', '{"dataVolumeCap":{"cap":null}}', '{}'):
        run(changes={"MONITORING_TEST_BILLING": value}, success=False, cli_calls=4)
    run(changes={"MONITORING_TEST_FAIL": "update"}, success=False, cli_calls=2)
    run(changes={"MONITORING_TEST_FAIL": "workspace-show"}, success=False, cli_calls=3)
    run(options=[], success=False, cli_calls=0)
    run(options=["--resource-group"], success=False, cli_calls=0)
    run(options=["--bogus"], success=False, cli_calls=0)

    for value in ("-1", "0.001", "NaN", "{}", "__UNRESOLVED__"):
        params["parameters"]["monitoringDailyCapGb"]["value"] = value
        parameters.write_text(json.dumps(params))
        run(success=False, cli_calls=0)

    required = ["Microsoft.Web", "Microsoft.Storage", "Microsoft.Insights",
                "Microsoft.OperationalInsights", "Microsoft.AlertsManagement"]

    def preflight(*, changes=None, success=True):
        calls.write_text("")
        result = subprocess.run(["bash", "scripts/check-azure-providers.sh"],
                                env={**base_env, **(changes or {})}, text=True, capture_output=True)
        assert (result.returncode == 0) == success, (result.stdout, result.stderr)
        records = [json.loads(line) for line in calls.read_text().splitlines()]
        # Strict shim accepts only provider reads here, never registration or another mutation.
        assert records == [["provider", "show", "--namespace", name, "--query",
                            "registrationState", "--output", "tsv"] for name in required], records
        return result.stdout + result.stderr

    preflight()
    for provider in required:
        output = preflight(changes={"MONITORING_TEST_PROVIDER_STATES": json.dumps({provider: "NotRegistered"})},
                           success=False)
        assert f"az provider register --namespace {provider} --wait --output none" in output, output
    preflight(changes={"MONITORING_TEST_PROVIDER_STATES": '{"Microsoft.AlertsManagement":"Registering"}'})
    preflight(changes={"MONITORING_TEST_PROVIDER_STATES": '{"Microsoft.AlertsManagement":""}'}, success=False)
    output = preflight(changes={"MONITORING_TEST_FAIL": "Microsoft.Insights"}, success=False)
    assert "Cannot read Microsoft.Insights registration" in output, output
    output = preflight(changes={"MONITORING_TEST_PROVIDER_STATES": json.dumps(
        {name: "NotRegistered" for name in required})}, success=False)
    assert output.count("A subscription operator should run:") == len(required), output

    workflow = Path(".github/workflows/deploy-production.yml").read_text()
    assert workflow.index("uses: azure/login@v2") < workflow.index("bash scripts/check-azure-providers.sh")
    assert workflow.index("bash scripts/check-azure-providers.sh") < workflow.index("uses: azure/arm-deploy@v2")

print("PASS: monitoring cap verification is repeatable, rejects invalid/mismatched caps, propagates CLI failures, and prints no credentials")
print("PASS: provider preflight reports all missing registrations before deployment and only reads Azure metadata")
PY

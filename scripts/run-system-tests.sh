#!/bin/bash
# Run all BCApps System Application test codeunits sequentially.
# Prints progress to stdout and writes a summary JSON to /tmp/test-results.json.
#
# Usage:
#   ./scripts/run-system-tests.sh [codeunit-ids-file]
#
# If no file is given, extracts IDs from the compiled test source.
# Requires: BC container running with all apps published.

set -euo pipefail

# --- Config ---
AUTH="admin:Admin123!"
INSTANCE="InstanceName"
ODATA="http://localhost:7048/$INSTANCE"
API_PORT=7052

# MS-disabled codeunits (from BCApps DisabledTests/*.json where method = "*")
MS_DISABLED=" 132517 132928 135016 135018 139146 "

# --- Get company ID ---
echo "=== BCApps System Application Tests ==="
echo "$(date '+%H:%M:%S') Connecting to BC..."

COMPANY_ID=$(docker exec aldirectcompile-bc-1 curl -s --max-time 10 -u "$AUTH" \
    "$ODATA/ODataV4/Company" 2>&1 | \
    python3 -c "import json,sys; print(json.load(sys.stdin)['value'][0]['Id'])" 2>/dev/null) || {
    echo "ERROR: Cannot connect to BC. Is the container running with apps published?"
    exit 1
}
API="http://localhost:$API_PORT/$INSTANCE/api/custom/automation/v1.0/companies($COMPANY_ID)"

# Verify Test Runner API
docker exec aldirectcompile-bc-1 curl -s -o /dev/null -w "" --max-time 5 -u "$AUTH" \
    "$API/codeunitRunRequests" 2>/dev/null || {
    echo "ERROR: Test Runner API not available. Is TestRunner.app published?"
    exit 1
}

# --- Load codeunit IDs ---
ID_FILE="${1:-/tmp/test-codeunit-ids.txt}"
if [ ! -f "$ID_FILE" ]; then
    echo "$(date '+%H:%M:%S') Extracting test codeunit IDs from source..."
    grep -r "Subtype = Test" /tmp/systest-patched/ --include="*.al" -l 2>/dev/null | while read f; do
        grep -oP "^codeunit \K\d+" "$f"
    done | sort -n > /tmp/test-codeunit-ids.txt
    ID_FILE="/tmp/test-codeunit-ids.txt"
fi

TOTAL_CUS=$(wc -l < "$ID_FILE")
echo "$(date '+%H:%M:%S') Found $TOTAL_CUS test codeunits"
echo ""

# --- Run tests ---
PASSED=0
FAILED=0
SKIPPED=0
ERRORS=0
RUN=0
FAILED_LIST=""
START_TIME=$(date +%s)

printf "%-8s %-8s %-50s %s\n" "Time" "Status" "Codeunit" "Detail"
printf "%-8s %-8s %-50s %s\n" "--------" "--------" "--------------------------------------------------" "------"

while IFS= read -r CU_ID; do
    RUN=$((RUN + 1))

    # Skip MS-disabled
    if echo "$MS_DISABLED" | grep -q " $CU_ID "; then
        SKIPPED=$((SKIPPED + 1))
        continue
    fi

    # Create run request
    RESP=$(docker exec aldirectcompile-bc-1 curl -s --max-time 15 -u "$AUTH" -X POST \
        -H "Content-Type: application/json" -d "{\"codeunitId\": $CU_ID}" \
        "$API/codeunitRunRequests" 2>&1)
    ID=$(echo "$RESP" | python3 -c "import json,sys; print(json.load(sys.stdin).get('Id',''))" 2>/dev/null)

    if [ -z "$ID" ] || [ "$ID" = "" ]; then
        ERRORS=$((ERRORS + 1))
        ELAPSED=$(( $(date +%s) - START_TIME ))
        printf "%-8s %-8s %-50s %s\n" "$(date '+%H:%M:%S')" "ERROR" "CU $CU_ID" "create failed (auth?)"
        continue
    fi

    # Execute
    docker exec aldirectcompile-bc-1 curl -s -o /dev/null --max-time 300 -u "$AUTH" -X POST \
        -H "Content-Type: application/json" \
        "$API/codeunitRunRequests($ID)/Microsoft.NAV.runCodeunit" 2>&1

    # Get result
    RESULT_JSON=$(docker exec aldirectcompile-bc-1 curl -s --max-time 10 -u "$AUTH" \
        "$API/codeunitRunRequests($ID)" 2>&1)
    RESULT=$(echo "$RESULT_JSON" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('LastResult','Unknown'))" 2>/dev/null)

    ELAPSED=$(( $(date +%s) - START_TIME ))
    ELAPSED_FMT=$(printf '%d:%02d' $((ELAPSED/60)) $((ELAPSED%60)))
    PROGRESS="[$RUN/$TOTAL_CUS]"

    if [ "$RESULT" = "Success" ]; then
        PASSED=$((PASSED + 1))
        printf "%-8s %-8s %-50s %s\n" "$(date '+%H:%M:%S')" "PASS" "CU $CU_ID" "$PROGRESS ${ELAPSED_FMT}m"
    else
        FAILED=$((FAILED + 1))
        FAILED_LIST="$FAILED_LIST $CU_ID"
        # Extract short error
        SHORT_ERR=$(echo "$RESULT" | head -c 60)
        printf "%-8s %-8s %-50s %s\n" "$(date '+%H:%M:%S')" "FAIL" "CU $CU_ID" "$SHORT_ERR $PROGRESS"
    fi
done < "$ID_FILE"

# --- Summary ---
END_TIME=$(date +%s)
DURATION=$(( END_TIME - START_TIME ))
DURATION_FMT=$(printf '%d:%02d' $((DURATION/60)) $((DURATION%60)))
EFFECTIVE=$((PASSED + FAILED))
PASS_RATE=0
[ $EFFECTIVE -gt 0 ] && PASS_RATE=$(python3 -c "print(f'{$PASSED/$EFFECTIVE*100:.1f}')" 2>/dev/null)

echo ""
echo "========================================="
echo "        TEST RESULTS SUMMARY"
echo "========================================="
echo "Duration:    ${DURATION_FMT}m (${DURATION}s)"
echo "Total:       $TOTAL_CUS codeunits"
echo "Passed:      $PASSED"
echo "Failed:      $FAILED"
echo "MS-Disabled: $SKIPPED"
echo "Auth errors: $ERRORS"
echo "Pass rate:   ${PASS_RATE}% (of ${EFFECTIVE} executed)"
echo "========================================="

if [ -n "$FAILED_LIST" ]; then
    echo ""
    echo "Failed codeunits:$FAILED_LIST"
fi

# Write JSON results
cat > /tmp/test-results.json << ENDJSON
{
  "timestamp": "$(date -Iseconds)",
  "duration_seconds": $DURATION,
  "total": $TOTAL_CUS,
  "passed": $PASSED,
  "failed": $FAILED,
  "skipped": $SKIPPED,
  "errors": $ERRORS,
  "pass_rate": $PASS_RATE,
  "failed_codeunits": [$(echo "$FAILED_LIST" | tr ' ' '\n' | grep . | sed 's/.*/"&"/' | paste -sd,)]
}
ENDJSON
echo ""
echo "Results written to /tmp/test-results.json"

#!/bin/bash
# Run all BCApps System Application test codeunits sequentially.
# Executes entirely inside the BC container for speed. Live progress output.
#
# Usage: ./scripts/run-system-tests.sh [codeunit-ids-file]

set -euo pipefail

ID_FILE="${1:-/tmp/test-codeunit-ids.txt}"
[ -f "$ID_FILE" ] || { echo "ERROR: $ID_FILE not found"; exit 1; }

TOTAL=$(wc -l < "$ID_FILE")
echo "=== BCApps System Application Tests ($TOTAL codeunits) ==="

# Copy test IDs into container
docker cp "$ID_FILE" aldirectcompile-bc-1:/tmp/test-ids.txt

# Run everything inside the container (avoids per-call docker exec overhead)
docker exec -i aldirectcompile-bc-1 bash << 'ENDSCRIPT'
AUTH="admin:Admin123!"
INSTANCE="InstanceName"
ODATA="http://localhost:7048/$INSTANCE"

# MS-disabled codeunits (method = "*" in BCApps DisabledTests)
MS_DISABLED=" 132517 132928 135016 135018 139146 "

# Get company ID
COMPANY_ID=$(curl -sf --max-time 10 -u "$AUTH" "$ODATA/ODataV4/Company" | \
    python3 -c "import json,sys; print(json.load(sys.stdin)['value'][0]['Id'])")
API="http://localhost:7052/$INSTANCE/api/custom/automation/v1.0/companies($COMPANY_ID)"

# Verify API
HTTP=$(curl -so /dev/null -w "%{http_code}" --max-time 10 -u "$AUTH" "$API/codeunitRunRequests")
[ "$HTTP" = "200" ] || { echo "ERROR: Test Runner API HTTP $HTTP"; exit 1; }

TOTAL=$(wc -l < /tmp/test-ids.txt)
PASSED=0; FAILED=0; SKIPPED=0; ERRORS=0; RUN=0
FAILED_LIST=""
START=$(date +%s)

printf "%-8s %-6s %-7s %s\n" "Time" "Status" "CU" "Detail"
echo "--------------------------------------------------------------"

while IFS= read -r CU_ID; do
    RUN=$((RUN + 1))

    # Skip MS-disabled
    if echo "$MS_DISABLED" | grep -q " $CU_ID "; then
        SKIPPED=$((SKIPPED + 1))
        continue
    fi

    # Create run request
    RESP=$(curl -sf --max-time 15 -u "$AUTH" -X POST \
        -H "Content-Type: application/json" -d "{\"codeunitId\": $CU_ID}" \
        "$API/codeunitRunRequests" 2>&1) || true
    ID=$(echo "$RESP" | python3 -c "import json,sys; print(json.load(sys.stdin).get('Id',''))" 2>/dev/null)

    if [ -z "$ID" ]; then
        ERRORS=$((ERRORS + 1))
        printf "%-8s %-6s %-7s %s\n" "$(date +%H:%M:%S)" "ERR" "$CU_ID" "create failed [$RUN/$TOTAL]"
        continue
    fi

    # Execute
    curl -sf -o /dev/null --max-time 600 -u "$AUTH" -X POST \
        -H "Content-Type: application/json" \
        "$API/codeunitRunRequests($ID)/Microsoft.NAV.runCodeunit" 2>/dev/null || true

    # Get result
    RESULT=$(curl -sf --max-time 10 -u "$AUTH" \
        "$API/codeunitRunRequests($ID)" 2>/dev/null | \
        python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('LastResult','Unknown'))" 2>/dev/null) || RESULT="Unknown"

    # ETA calculation
    ELAPSED=$(( $(date +%s) - START ))
    ETA=""
    if [ $RUN -gt 5 ] && [ $ELAPSED -gt 0 ]; then
        REMAINING=$(( (TOTAL - RUN) * ELAPSED / RUN ))
        ETA=" ETA $(printf '%d:%02d' $((REMAINING/60)) $((REMAINING%60)))"
    fi

    if [ "$RESULT" = "Success" ]; then
        PASSED=$((PASSED + 1))
        printf "%-8s %-6s %-7s %s\n" "$(date +%H:%M:%S)" "PASS" "$CU_ID" "[$RUN/$TOTAL p=$PASSED f=$FAILED$ETA]"
    else
        FAILED=$((FAILED + 1))
        FAILED_LIST="$FAILED_LIST $CU_ID"
        SHORT=$(echo "$RESULT" | head -c 45)
        printf "%-8s %-6s %-7s %s\n" "$(date +%H:%M:%S)" "FAIL" "$CU_ID" "$SHORT [$RUN/$TOTAL$ETA]"
    fi
done < /tmp/test-ids.txt

DUR=$(( $(date +%s) - START ))
EFF=$((PASSED + FAILED))
RATE=0; [ $EFF -gt 0 ] && RATE=$(python3 -c "print(round($PASSED/$EFF*100,1))")

echo ""
echo "========================================="
echo "        TEST RESULTS SUMMARY"
echo "========================================="
printf "Duration:    %d:%02d\n" $((DUR/60)) $((DUR%60))
echo "Total:       $TOTAL codeunits"
echo "Passed:      $PASSED"
echo "Failed:      $FAILED"
echo "MS-Disabled: $SKIPPED"
echo "Auth errors: $ERRORS"
echo "Pass rate:   ${RATE}% (of $EFF executed)"
echo "========================================="
[ -n "$FAILED_LIST" ] && echo "Failed:$FAILED_LIST"
ENDSCRIPT

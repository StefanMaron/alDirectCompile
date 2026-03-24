#!/bin/bash
# Run BCApps System Application test codeunits using MS Test Framework (130451).
# Executes inside the BC container for speed. Live progress output.
#
# Uses our TestRunner Extension which wraps the MS Test Suite infrastructure:
# - Creates AL Test Suite with codeunit 130451 (disabled isolation)
# - Discovers test methods via codeunit 130452
# - Results tracked in Test Method Line table
#
# Usage: ./scripts/run-system-tests.sh [codeunit-ids-file]

set -euo pipefail

ID_FILE="${1:-/tmp/test-codeunit-ids.txt}"
[ -f "$ID_FILE" ] || { echo "ERROR: $ID_FILE not found"; exit 1; }

TOTAL=$(wc -l < "$ID_FILE")
echo "=== BCApps System Application Tests ($TOTAL codeunits) ==="
echo "    Using MS Test Framework (codeunit 130451, disabled isolation)"

# Copy test IDs into container
docker cp "$ID_FILE" aldirectcompile-bc-1:/tmp/test-ids.txt

PASSED=0; FAILED=0; SKIPPED=0; ERRORS=0; TIMEOUTS=0; RUN=0
FAILED_LIST=""
TIMEOUT_LIST=""
START=$(date +%s)
# Per-test timeout in seconds
TEST_TIMEOUT=120

# MS-disabled codeunits (method = "*" in BCApps DisabledTests/*.json)
MS_DISABLED=" 132517 132928 135016 135018 139146 "

# Get API base URL from inside the container
API_INFO=$(docker exec aldirectcompile-bc-1 bash -c '
AUTH="admin:Admin123!"
ODATA="http://localhost:7048/InstanceName/ODataV4"
CID=$(curl -sf --max-time 10 -u "$AUTH" "$ODATA/Company" | python3 -c "import json,sys; print(json.load(sys.stdin)[\"value\"][0][\"Id\"])")
echo "$CID"
')
COMPANY_ID="$API_INFO"

printf "%-8s %-7s %-7s %s\n" "Time" "Status" "CU" "Detail"
echo "--------------------------------------------------------------"

while IFS= read -r CU_ID; do
    RUN=$((RUN + 1))

    # Skip MS-disabled (whole codeunits with method="*")
    if echo "$MS_DISABLED" | grep -q " $CU_ID "; then
        SKIPPED=$((SKIPPED + 1))
        printf "%-8s %-7s %-7s %s\n" "$(date +%H:%M:%S)" "SKIP" "$CU_ID" "MS-disabled [$RUN/$TOTAL]"
        continue
    fi

    # Run test via docker exec with timeout
    RESULT=$(timeout $TEST_TIMEOUT docker exec aldirectcompile-bc-1 bash -c "
        AUTH='admin:Admin123!'
        API='http://localhost:7052/InstanceName/api/custom/automation/v1.0/companies($COMPANY_ID)/codeunitRunRequests'

        # Create request
        RESP=\$(curl -sf --max-time 15 -u \"\$AUTH\" -X POST \
            -H 'Content-Type: application/json' -d '{\"codeunitId\": $CU_ID}' \
            \"\$API\" 2>/dev/null) || { echo 'CREATE_FAILED'; exit 0; }
        ID=\$(echo \"\$RESP\" | python3 -c \"import json,sys; print(json.load(sys.stdin).get('Id',''))\" 2>/dev/null)
        [ -z \"\$ID\" ] && { echo 'CREATE_FAILED'; exit 0; }

        # Execute
        curl -sf -o /dev/null --max-time $((TEST_TIMEOUT - 10)) -u \"\$AUTH\" -X POST \
            -H 'Content-Type: application/json' \
            \"\$API(\$ID)/Microsoft.NAV.runCodeunit\" 2>/dev/null || true

        # Get result
        curl -sf --max-time 10 -u \"\$AUTH\" \"\$API(\$ID)\" 2>/dev/null | \
            python3 -c \"import json,sys; d=json.load(sys.stdin); print(d.get('LastResult','Unknown'))\" 2>/dev/null || echo 'Unknown'
    " 2>/dev/null) || RESULT=""

    # ETA calculation
    ELAPSED=$(( $(date +%s) - START ))
    ETA=""
    if [ $RUN -gt 5 ] && [ $ELAPSED -gt 0 ]; then
        REMAINING=$(( (TOTAL - RUN) * ELAPSED / RUN ))
        ETA=" ETA $(printf '%d:%02d' $((REMAINING/60)) $((REMAINING%60)))"
    fi

    if [ -z "$RESULT" ] || [ "$RESULT" = "" ]; then
        TIMEOUTS=$((TIMEOUTS + 1))
        TIMEOUT_LIST="$TIMEOUT_LIST $CU_ID"
        printf "%-8s %-7s %-7s %s\n" "$(date +%H:%M:%S)" "TMOUT" "$CU_ID" "timeout ${TEST_TIMEOUT}s [$RUN/$TOTAL$ETA]"
    elif [ "$RESULT" = "CREATE_FAILED" ]; then
        ERRORS=$((ERRORS + 1))
        printf "%-8s %-7s %-7s %s\n" "$(date +%H:%M:%S)" "ERR" "$CU_ID" "create failed [$RUN/$TOTAL$ETA]"
    elif [ "$RESULT" = "Success" ]; then
        PASSED=$((PASSED + 1))
        printf "%-8s %-7s %-7s %s\n" "$(date +%H:%M:%S)" "PASS" "$CU_ID" "[$RUN/$TOTAL p=$PASSED f=$FAILED$ETA]"
    else
        FAILED=$((FAILED + 1))
        FAILED_LIST="$FAILED_LIST $CU_ID"
        SHORT=$(echo "$RESULT" | head -c 45)
        printf "%-8s %-7s %-7s %s\n" "$(date +%H:%M:%S)" "FAIL" "$CU_ID" "$SHORT [$RUN/$TOTAL$ETA]"
    fi
done < "$ID_FILE"

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
echo "Timeouts:    $TIMEOUTS"
echo "Errors:      $ERRORS"
echo "Pass rate:   ${RATE}% (of $EFF executed)"
echo "========================================="
[ -n "$FAILED_LIST" ] && echo "Failed:$FAILED_LIST"
[ -n "$TIMEOUT_LIST" ] && echo "Timeouts:$TIMEOUT_LIST"

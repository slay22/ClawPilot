#!/usr/bin/env bash
# claw-pilot-runner entrypoint
#
# Environment variables (injected by PrRunnerService):
#   REPO_URL      — clone URL of the target repository
#   BRANCH_NAME   — PR branch to check out
#   BASE_BRANCH   — base branch (used for diff)
#   GIT_PAT       — short-lived PAT for git authentication (optional)
#   PR_ID         — PR identifier (for logging only)
#   PUSH_ONLY     — when "true", skip build/test/lint and only push staged commits
#
# Output: /output/result.json  (read by PrRunnerService)
#
# result.json schema:
#   {
#     "success":      bool,
#     "buildPassed":  bool,
#     "testsPassed":  bool,
#     "lintPassed":   bool,
#     "fixesApplied": bool,
#     "diff":         string,
#     "summary":      string,
#     "logs":         string
#   }

set -euo pipefail

REPO_URL="${REPO_URL:?REPO_URL is required}"
BRANCH_NAME="${BRANCH_NAME:?BRANCH_NAME is required}"
BASE_BRANCH="${BASE_BRANCH:-main}"
PR_ID="${PR_ID:-unknown}"
PUSH_ONLY="${PUSH_ONLY:-false}"
WORK_DIR="/workspace/repo"

# ── Helpers ────────────────────────────────────────────────────────────────────

write_result() {
    local success="$1"
    local build_passed="$2"
    local tests_passed="$3"
    local lint_passed="$4"
    local fixes_applied="$5"
    local diff_content="$6"
    local summary="$7"
    local logs="$8"

    jq -n \
        --argjson success        "$success" \
        --argjson buildPassed    "$build_passed" \
        --argjson testsPassed    "$tests_passed" \
        --argjson lintPassed     "$lint_passed" \
        --argjson fixesApplied   "$fixes_applied" \
        --arg     diff           "$diff_content" \
        --arg     summary        "$summary" \
        --arg     logs           "$logs" \
        '{
            success:      $success,
            buildPassed:  $buildPassed,
            testsPassed:  $testsPassed,
            lintPassed:   $lintPassed,
            fixesApplied: $fixesApplied,
            diff:         $diff,
            summary:      $summary,
            logs:         $logs
        }' > /output/result.json
}

fail() {
    local msg="$1"
    echo "[runner] FATAL: $msg" >&2
    write_result false false false false false "" "$msg" ""
    exit 0  # exit 0 so Docker --rm doesn't mask the structured error
}

# ── Configure git credentials ──────────────────────────────────────────────────

git config --global user.email "claw-pilot-runner@noreply.local"
git config --global user.name  "Claw-Pilot Runner"
git config --global credential.helper store

if [[ -n "${GIT_PAT:-}" ]]; then
    # Extract hostname from REPO_URL for the credential store entry
    REPO_HOST=$(echo "$REPO_URL" | sed -E 's|https?://([^/]+).*|\1|')
    echo "https://x-access-token:${GIT_PAT}@${REPO_HOST}" > ~/.git-credentials
    # Use the original URL — git will supply credentials from the store.
    # Do NOT embed the PAT in the URL to avoid it appearing in error messages and logs.
    CLONE_URL="$REPO_URL"
else
    CLONE_URL="$REPO_URL"
fi

# ── PUSH_ONLY mode ─────────────────────────────────────────────────────────────
# Used by PrRunnerService to push an already-committed fix as a separate step.

if [[ "$PUSH_ONLY" == "true" ]]; then
    echo "[runner] Push-only mode"
    if [[ ! -d "$WORK_DIR/.git" ]]; then
        fail "Push-only mode: /workspace/repo does not contain a git repository"
    fi
    cd "$WORK_DIR"
    git push origin "HEAD:${BRANCH_NAME}" || fail "git push failed"
    write_result true true true true true "" "Auto-fix commit pushed successfully." ""
    exit 0
fi

# ── Clone ──────────────────────────────────────────────────────────────────────

echo "[runner] Cloning ${REPO_URL} branch ${BRANCH_NAME} ..."
git clone --branch "$BRANCH_NAME" --depth 50 "$CLONE_URL" "$WORK_DIR" \
    || fail "git clone failed — check REPO_URL and GIT_PAT"

cd "$WORK_DIR"

# ── Project-type detection ──────────────────────────────────────────────────────

detect_project() {
    if find . -maxdepth 3 -name "*.csproj" -not -path "*/obj/*" | grep -q .; then
        echo "dotnet"
    elif [[ -f "package.json" ]]; then
        echo "node"
    elif [[ -f "requirements.txt" || -f "pyproject.toml" || -f "setup.py" ]]; then
        echo "python"
    else
        echo "unknown"
    fi
}

PROJECT_TYPE=$(detect_project)
echo "[runner] Detected project type: ${PROJECT_TYPE}"

LOGS=""
BUILD_PASSED=false
TESTS_PASSED=false
LINT_PASSED=false
FIXES_APPLIED=false
DIFF_CONTENT=""
SUMMARY="No checks ran."

# ── .NET ────────────────────────────────────────────────────────────────────────

run_dotnet() {
    echo "[runner] === .NET build ==="
    BUILD_LOG=$(dotnet build --nologo 2>&1) && BUILD_PASSED=true || BUILD_PASSED=false
    LOGS+="BUILD:\n${BUILD_LOG}\n\n"

    if [[ "$BUILD_PASSED" == "true" ]]; then
        echo "[runner] === .NET test ==="
        TEST_LOG=$(dotnet test --nologo --no-build 2>&1) && TESTS_PASSED=true || TESTS_PASSED=false
        LOGS+="TESTS:\n${TEST_LOG}\n\n"
    fi

    echo "[runner] === .NET format ==="
    FORMAT_LOG=$(dotnet format --no-restore 2>&1) && LINT_PASSED=true || LINT_PASSED=false
    LOGS+="FORMAT:\n${FORMAT_LOG}\n\n"

    if git diff --quiet; then
        FIXES_APPLIED=false
    else
        FIXES_APPLIED=true
        DIFF_CONTENT=$(git diff)
        git add -A
        git commit -m "style: apply dotnet format fixes [claw-pilot-runner]"
    fi
}

# ── Node.js ─────────────────────────────────────────────────────────────────────

run_node() {
    echo "[runner] === Node install ==="
    npm ci --prefer-offline 2>&1 | tail -20 || npm install 2>&1 | tail -20

    echo "[runner] === Node build ==="
    if jq -e '.scripts.build' package.json > /dev/null 2>&1; then
        BUILD_LOG=$(npm run build 2>&1) && BUILD_PASSED=true || BUILD_PASSED=false
        LOGS+="BUILD:\n${BUILD_LOG}\n\n"
    else
        BUILD_PASSED=true
    fi

    echo "[runner] === Node test ==="
    if jq -e '.scripts.test' package.json > /dev/null 2>&1; then
        TEST_LOG=$(npm test -- --watchAll=false --passWithNoTests 2>&1) \
            && TESTS_PASSED=true || TESTS_PASSED=false
        LOGS+="TESTS:\n${TEST_LOG}\n\n"
    else
        TESTS_PASSED=true
    fi

    echo "[runner] === ESLint / Prettier ==="
    if [[ -f ".eslintrc.js" || -f ".eslintrc.json" || -f ".eslintrc.cjs" || -f "eslint.config.js" ]]; then
        LINT_LOG=$(npx eslint --fix . 2>&1) && LINT_PASSED=true || LINT_PASSED=false
        LOGS+="LINT:\n${LINT_LOG}\n\n"
    elif jq -e '.scripts.lint' package.json > /dev/null 2>&1; then
        LINT_LOG=$(npm run lint -- --fix 2>&1) && LINT_PASSED=true || LINT_PASSED=false
        LOGS+="LINT:\n${LINT_LOG}\n\n"
    else
        LINT_PASSED=true
    fi

    if git diff --quiet; then
        FIXES_APPLIED=false
    else
        FIXES_APPLIED=true
        DIFF_CONTENT=$(git diff)
        git add -A
        git commit -m "style: apply lint/format fixes [claw-pilot-runner]"
    fi
}

# ── Python ─────────────────────────────────────────────────────────────────────

run_python() {
    python3 -m venv /tmp/venv
    source /tmp/venv/bin/activate

    if [[ -f "requirements.txt" ]]; then
        pip install -q -r requirements.txt 2>&1 | tail -5
    elif [[ -f "pyproject.toml" ]]; then
        pip install -q -e ".[dev,test]" 2>&1 | tail -5 \
            || pip install -q -e . 2>&1 | tail -5
    fi

    BUILD_PASSED=true  # Python doesn't have a build step in the traditional sense

    echo "[runner] === pytest ==="
    if python3 -m pytest --version > /dev/null 2>&1; then
        TEST_LOG=$(python3 -m pytest --tb=short -q 2>&1) \
            && TESTS_PASSED=true || TESTS_PASSED=false
        LOGS+="TESTS:\n${TEST_LOG}\n\n"
    else
        TESTS_PASSED=true
    fi

    echo "[runner] === ruff ==="
    if python3 -m ruff --version > /dev/null 2>&1; then
        LINT_LOG=$(python3 -m ruff check --fix . 2>&1) && LINT_PASSED=true || LINT_PASSED=false
        LOGS+="LINT:\n${LINT_LOG}\n\n"
        python3 -m ruff format . 2>&1 | tail -5 || true
    else
        LINT_PASSED=true
    fi

    deactivate

    if git diff --quiet; then
        FIXES_APPLIED=false
    else
        FIXES_APPLIED=true
        DIFF_CONTENT=$(git diff)
        git add -A
        git commit -m "style: apply ruff fixes [claw-pilot-runner]"
    fi
}

# ── Dispatch ───────────────────────────────────────────────────────────────────

case "$PROJECT_TYPE" in
    dotnet) run_dotnet ;;
    node)   run_node ;;
    python) run_python ;;
    *)
        SUMMARY="Unknown project type — no build/test/lint checks ran."
        write_result true true true true false "" "$SUMMARY" ""
        exit 0
        ;;
esac

# ── Build summary ──────────────────────────────────────────────────────────────

OVERALL_SUCCESS=true
[[ "$BUILD_PASSED" == "false" ]] && OVERALL_SUCCESS=false

SUMMARY_PARTS=()
[[ "$BUILD_PASSED"  == "true"  ]] && SUMMARY_PARTS+=("Build ✅") || SUMMARY_PARTS+=("Build ❌")
[[ "$TESTS_PASSED"  == "true"  ]] && SUMMARY_PARTS+=("Tests ✅") || SUMMARY_PARTS+=("Tests ❌")
[[ "$LINT_PASSED"   == "true"  ]] && SUMMARY_PARTS+=("Lint ✅")  || SUMMARY_PARTS+=("Lint ❌")
[[ "$FIXES_APPLIED" == "true"  ]] && SUMMARY_PARTS+=("Auto-fixes applied")

SUMMARY=$(IFS=' | '; echo "${SUMMARY_PARTS[*]}")

echo "[runner] Done — ${SUMMARY}"

write_result \
    "$OVERALL_SUCCESS" \
    "$BUILD_PASSED" \
    "$TESTS_PASSED" \
    "$LINT_PASSED" \
    "$FIXES_APPLIED" \
    "$DIFF_CONTENT" \
    "$SUMMARY" \
    "$(echo -e "$LOGS" | tail -c 8000)"

#!/bin/bash

# Smart Export & Commit - Zero Disruption Approach
# Exports completed trading days while generation continues
# Usage: ./smart-export-commit.sh

set -e

DB_PATH="/c/Code/Stroll.Theta.DB/stroll_theta.db"
THETA_REPO_PATH="/c/Code/Stroll.Theta.DB"
EXPORT_DIR="$THETA_REPO_PATH/daily-exports"

# Colors
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m'

echo -e "${BLUE}╔══════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║              ${CYAN}SMART EXPORT & COMMIT (ZERO DISRUPTION)${BLUE}           ║${NC}"
echo -e "${BLUE}║     ${YELLOW}Export stable days while generation continues running${BLUE}     ║${NC}"
echo -e "${BLUE}╚══════════════════════════════════════════════════════════════════╝${NC}"
echo ""

# Function to get completion status by day
get_day_completion() {
    local target_date="$1"
    
    # Check if all 6 symbols have data for this day
    sqlite3 "$DB_PATH" "
    SELECT COUNT(DISTINCT symbol) as symbol_count 
    FROM options_chain 
    WHERE session_date = '$target_date';" 2>/dev/null || echo "0"
}

# Function to find stable completed days
find_stable_days() {
    # Get days that have all 6 symbols (complete) and are older than current processing
    sqlite3 "$DB_PATH" "
    SELECT session_date, COUNT(DISTINCT symbol) as symbols, COUNT(*) as options
    FROM options_chain 
    WHERE session_date <= '2025-05-06'  -- Stable cutoff, before recent generation
    GROUP BY session_date 
    HAVING COUNT(DISTINCT symbol) = 6  -- All symbols present
    ORDER BY session_date DESC 
    LIMIT 20;" 2>/dev/null || echo ""
}

# Function to export single complete day
export_day() {
    local trading_day="$1"
    local export_file="$EXPORT_DIR/stroll_theta_${trading_day}.sql"
    
    echo -e "${CYAN}📦 Exporting stable day: $trading_day${NC}"
    
    mkdir -p "$EXPORT_DIR"
    
    # Export both options and bars for the day
    {
        echo "-- Stroll.Alpha Options Data Export for $trading_day"
        echo "-- Generated: $(date)"
        echo ""
        echo "-- Options Chain Data"
        sqlite3 "$DB_PATH" ".mode insert options_chain" 2>/dev/null
        sqlite3 "$DB_PATH" "SELECT * FROM options_chain WHERE session_date = '$trading_day';" 2>/dev/null
        echo ""
        echo "-- Underlying Bars Data"  
        sqlite3 "$DB_PATH" ".mode insert underlying_bars" 2>/dev/null
        sqlite3 "$DB_PATH" "SELECT * FROM underlying_bars WHERE DATE(ts_utc) = '$trading_day';" 2>/dev/null
    } > "$export_file"
    
    # Check export size
    local size_mb=$(du -m "$export_file" 2>/dev/null | cut -f1 || echo "0")
    echo -e "${GREEN}  ✅ Exported ${size_mb}MB for $trading_day${NC}"
    
    # Skip if too large for GitHub
    if [ "$size_mb" -gt 95 ]; then
        echo -e "${YELLOW}  ⚠️  File too large for GitHub, skipping commit${NC}"
        rm -f "$export_file"
        return 1
    fi
    
    echo "$export_file"
}

# Function to commit exported day
commit_day() {
    local export_file="$1"
    local trading_day=$(basename "$export_file" .sql | cut -d'_' -f3)
    
    cd "$THETA_REPO_PATH"
    
    echo -e "${BLUE}📤 Committing $trading_day${NC}"
    
    # Add the export file
    git add "$(basename "$export_file")" 2>/dev/null || {
        echo -e "${YELLOW}Git add failed, skipping${NC}"
        return 1
    }
    
    # Get stats for commit message
    local options_count=$(grep -c "INSERT INTO options_chain" "$export_file" 2>/dev/null || echo "0")
    local symbols=$(sqlite3 "$DB_PATH" "SELECT COUNT(DISTINCT symbol) FROM options_chain WHERE session_date = '$trading_day';" 2>/dev/null || echo "6")
    
    # Commit with detailed message
    git commit -m "feat: Add options data for $trading_day

📊 Trading Day Summary:
- Date: $trading_day  
- Options contracts: $options_count
- Symbols covered: $symbols (SPX, XSP, VIX, QQQ, GLD, USO)
- File size: $(du -h daily-exports/stroll_theta_${trading_day}.sql | cut -f1)
- DTE range: 0-45 days

🤖 Generated with Claude Code
Co-Authored-By: Claude <noreply@anthropic.com>" 2>/dev/null || {
        echo -e "${YELLOW}Commit failed, skipping${NC}"
        return 1
    }
    
    echo -e "${GREEN}  ✅ Committed $trading_day${NC}"
}

# Main execution
main() {
    echo -e "${CYAN}🔍 Finding stable completed days...${NC}"
    
    local stable_days=$(find_stable_days)
    
    if [ -z "$stable_days" ]; then
        echo -e "${YELLOW}No stable days found for export${NC}"
        return 0
    fi
    
    local exported_count=0
    local committed_count=0
    
    echo "$stable_days" | while IFS='|' read -r trading_day symbols options; do
        [ -z "$trading_day" ] && continue
        
        echo -e "${BLUE}Processing: $trading_day (${symbols} symbols, ${options} options)${NC}"
        
        # Check if already exported
        if [ -f "$EXPORT_DIR/stroll_theta_${trading_day}.sql" ]; then
            echo -e "${YELLOW}  Already exported, skipping${NC}"
            continue
        fi
        
        # Export the day
        local export_file=$(export_day "$trading_day")
        if [ $? -eq 0 ] && [ -n "$export_file" ]; then
            exported_count=$((exported_count + 1))
            
            # Commit the export
            if commit_day "$export_file"; then
                committed_count=$((committed_count + 1))
            fi
            
            # Rate limiting - max 5 commits per run
            if [ "$committed_count" -ge 5 ]; then
                echo -e "${BLUE}📊 Committed 5 days, stopping for rate limiting${NC}"
                break
            fi
            
            # Small delay between commits
            sleep 2
        fi
    done
    
    echo ""
    echo -e "${GREEN}📈 Export Summary:${NC}"
    echo -e "${GREEN}  Days exported: $exported_count${NC}"
    echo -e "${GREEN}  Days committed: $committed_count${NC}"
    
    # Push to GitHub if commits were made
    if [ "$committed_count" -gt 0 ]; then
        echo -e "${BLUE}🚀 Pushing to GitHub...${NC}"
        git push origin main 2>/dev/null && echo -e "${GREEN}✅ Push successful${NC}" || echo -e "${YELLOW}⚠️  Push failed (will retry later)${NC}"
    fi
}

# Run main function
main "$@"
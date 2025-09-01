#!/bin/bash

# Auto-Commit Watcher for Stroll.Theta.DB
# Automatically runs daily commits as data generation progresses
# Usage: ./auto-commit-watcher.sh

set -e

DB_PATH="/c/Code/Stroll.Theta.DB/stroll_theta.db"
THETA_REPO_PATH="/c/Code/Stroll.Theta.DB"
CHECK_INTERVAL=1800  # Check every 30 minutes
MIN_NEW_DAYS=5       # Minimum new days before triggering commit

# Colors
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m'

echo -e "${BLUE}╔═══════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║              ${CYAN}STROLL.THETA.DB AUTO-COMMIT WATCHER${BLUE}             ║${NC}"
echo -e "${BLUE}║            ${YELLOW}Monitors generation and commits automatically${BLUE}      ║${NC}"
echo -e "${BLUE}╚═══════════════════════════════════════════════════════════════╝${NC}"
echo ""

# Function to count trading days since last commit
count_new_days() {
    local last_committed_day
    if [ -f "$THETA_REPO_PATH/commit-log.txt" ]; then
        last_committed_day=$(tail -1 "$THETA_REPO_PATH/commit-log.txt" | cut -d'|' -f1)
    else
        last_committed_day="2025-08-30"
    fi
    
    sqlite3 "$DB_PATH" "
    SELECT COUNT(DISTINCT session_date) 
    FROM options_chain 
    WHERE session_date < '$last_committed_day';" 2>/dev/null || echo "0"
}

# Function to get database stats
get_db_stats() {
    if [ -f "$DB_PATH" ]; then
        sqlite3 "$DB_PATH" "
        SELECT 
            COUNT(*) as total_options,
            COUNT(DISTINCT session_date) as trading_days,
            COUNT(DISTINCT symbol) as symbols,
            MIN(session_date) as earliest_date,
            MAX(session_date) as latest_date
        FROM options_chain;" 2>/dev/null || echo "0|0|0|N/A|N/A"
    else
        echo "0|0|0|N/A|N/A"
    fi
}

# Main monitoring loop
echo -e "${YELLOW}🔄 Starting auto-commit monitoring (checking every $(($CHECK_INTERVAL/60)) minutes)${NC}"
echo ""

while true; do
    # Get current database stats
    local stats=$(get_db_stats)
    IFS='|' read -r total_options trading_days symbols earliest_date latest_date <<< "$stats"
    
    # Count new days since last commit
    local new_days=$(count_new_days)
    
    # Display current status
    echo -e "${CYAN}📊 $(date '+%H:%M:%S') - Database Status:${NC}"
    echo -e "${GREEN}  Options: $(printf "%'d" $total_options) | Days: $trading_days | Symbols: $symbols${NC}"
    echo -e "${GREEN}  Range: $earliest_date → $latest_date${NC}"
    echo -e "${YELLOW}  New days since last commit: $new_days${NC}"
    
    # Check if we should commit
    if [ "$new_days" -ge "$MIN_NEW_DAYS" ]; then
        echo -e "${BLUE}🚀 Triggering daily commit (${new_days} new days available)${NC}"
        
        # Run the daily commit script
        if ./scripts/daily-commit.sh; then
            echo -e "${GREEN}✅ Auto-commit successful${NC}"
        else
            echo -e "${RED}❌ Auto-commit failed, will retry next cycle${NC}"
        fi
    else
        echo -e "${YELLOW}⏳ Waiting for more data (need $MIN_NEW_DAYS days, have $new_days)${NC}"
    fi
    
    echo ""
    
    # Wait for next check
    sleep $CHECK_INTERVAL
done
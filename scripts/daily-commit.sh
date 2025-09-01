#!/bin/bash

# Daily Commit Script for Stroll.Theta.DB
# Commits database changes one trading day at a time to avoid GitHub limits
# Usage: ./daily-commit.sh

set -e

# Configuration
DB_PATH="/c/Code/Stroll.Theta.DB/stroll_theta.db"
THETA_REPO_PATH="/c/Code/Stroll.Theta.DB"
MAX_COMMIT_SIZE_MB=95  # Stay under GitHub's 100MB limit
BATCH_DELAY_SECONDS=30 # Delay between commits to respect rate limits

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

echo -e "${BLUE}╔═══════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║                  ${CYAN}STROLL.THETA.DB DAILY COMMITTER${BLUE}                ║${NC}"
echo -e "${BLUE}║           ${YELLOW}Smart batching to respect GitHub limits${BLUE}           ║${NC}"
echo -e "${BLUE}╚═══════════════════════════════════════════════════════════════╝${NC}"
echo ""

# Function to get database file size in MB
get_db_size_mb() {
    if [ -f "$DB_PATH" ]; then
        local size_bytes=$(stat -f%z "$DB_PATH" 2>/dev/null || stat -c%s "$DB_PATH" 2>/dev/null || echo "0")
        echo $((size_bytes / 1024 / 1024))
    else
        echo "0"
    fi
}

# Function to get latest committed trading day
get_last_committed_day() {
    cd "$THETA_REPO_PATH"
    if [ -f "commit-log.txt" ]; then
        tail -1 commit-log.txt | cut -d'|' -f1 || echo "2025-08-30"
    else
        echo "2025-08-30"  # Start from day after our latest data
    fi
}

# Function to get next trading day to commit
get_next_trading_day() {
    local last_day="$1"
    
    # Query database for the next earliest trading day after last_day
    sqlite3 "$DB_PATH" "
    SELECT MIN(session_date) 
    FROM options_chain 
    WHERE session_date < '$last_day'
    ORDER BY session_date DESC
    LIMIT 1;" 2>/dev/null || echo ""
}

# Function to export single day data
export_single_day() {
    local trading_day="$1"
    local export_file="/tmp/stroll_theta_${trading_day}.sql"
    
    echo -e "${CYAN}📦 Exporting data for $trading_day...${NC}"
    
    # Export options chain for the specific day
    sqlite3 "$DB_PATH" <<EOF > "$export_file"
.mode insert options_chain
SELECT * FROM options_chain WHERE session_date = '$trading_day';
.mode insert underlying_bars  
SELECT * FROM underlying_bars WHERE DATE(ts_utc) = '$trading_day';
EOF
    
    # Check export file size
    local export_size_mb=$(du -m "$export_file" | cut -f1)
    echo -e "${GREEN}  Exported ${export_size_mb}MB for $trading_day${NC}"
    
    if [ "$export_size_mb" -gt "$MAX_COMMIT_SIZE_MB" ]; then
        echo -e "${RED}⚠️  Export too large (${export_size_mb}MB), skipping...${NC}"
        rm -f "$export_file"
        return 1
    fi
    
    echo "$export_file"
}

# Function to commit single day
commit_single_day() {
    local trading_day="$1"
    local export_file="$2"
    
    cd "$THETA_REPO_PATH"
    
    echo -e "${YELLOW}📤 Committing $trading_day to GitHub...${NC}"
    
    # Copy export file to repo
    cp "$export_file" "daily-exports/data_${trading_day}.sql"
    
    # Add to git
    git add "daily-exports/data_${trading_day}.sql"
    git add stroll_theta.db
    
    # Create commit message
    local options_count=$(sqlite3 "$DB_PATH" "SELECT COUNT(*) FROM options_chain WHERE session_date = '$trading_day';")
    local symbols_count=$(sqlite3 "$DB_PATH" "SELECT COUNT(DISTINCT symbol) FROM options_chain WHERE session_date = '$trading_day';")
    
    git commit -m "feat: Add trading day $trading_day

📊 Data Summary:
- Date: $trading_day
- Options contracts: $options_count
- Symbols: $symbols_count
- File size: $(du -h daily-exports/data_${trading_day}.sql | cut -f1)

🤖 Generated with Claude Code
Co-Authored-By: Claude <noreply@anthropic.com>"

    # Log the commit
    echo "$trading_day|$options_count|$symbols_count|$(date)" >> commit-log.txt
    
    echo -e "${GREEN}✅ Committed $trading_day successfully${NC}"
    
    # Cleanup
    rm -f "$export_file"
}

# Main execution
main() {
    # Ensure we're in the right directory and repo exists
    if [ ! -d "$THETA_REPO_PATH/.git" ]; then
        echo -e "${RED}Error: $THETA_REPO_PATH is not a git repository${NC}"
        exit 1
    fi
    
    # Create daily exports directory
    cd "$THETA_REPO_PATH"
    mkdir -p daily-exports
    
    # Check current database size
    local current_db_size=$(get_db_size_mb)
    echo -e "${CYAN}📊 Current database size: ${current_db_size}MB${NC}"
    
    if [ "$current_db_size" -gt "$MAX_COMMIT_SIZE_MB" ]; then
        echo -e "${YELLOW}Database too large for single commit, using daily batching...${NC}"
        
        # Get the range of days to commit
        local last_committed=$(get_last_committed_day)
        echo -e "${CYAN}📅 Last committed day: $last_committed${NC}"
        
        local commits_made=0
        local current_day="$last_committed"
        
        while true; do
            current_day=$(get_next_trading_day "$current_day")
            
            if [ -z "$current_day" ]; then
                echo -e "${GREEN}🎉 All available trading days have been committed!${NC}"
                break
            fi
            
            echo -e "${BLUE}Processing trading day: $current_day${NC}"
            
            # Export the day's data
            local export_file=$(export_single_day "$current_day")
            if [ $? -ne 0 ]; then
                continue
            fi
            
            # Commit the day
            commit_single_day "$current_day" "$export_file"
            commits_made=$((commits_made + 1))
            
            # Rate limiting - pause between commits
            if [ "$commits_made" -lt 10 ]; then  # Only commit up to 10 days per run
                echo -e "${YELLOW}⏳ Waiting ${BATCH_DELAY_SECONDS}s before next commit...${NC}"
                sleep "$BATCH_DELAY_SECONDS"
            else
                echo -e "${YELLOW}📝 Committed $commits_made days. Run again to continue...${NC}"
                break
            fi
        done
        
    else
        echo -e "${GREEN}Database small enough for direct commit${NC}"
        cd "$THETA_REPO_PATH"
        git add stroll_theta.db
        git commit -m "feat: Update Stroll.Theta.DB with latest data

$(sqlite3 "$DB_PATH" "SELECT 'Options: ' || COUNT(*) || ' contracts across ' || COUNT(DISTINCT session_date) || ' trading days' FROM options_chain;")

🤖 Generated with Claude Code
Co-Authored-By: Claude <noreply@anthropic.com>"
    fi
    
    # Push to GitHub
    echo -e "${BLUE}🚀 Pushing to GitHub...${NC}"
    git push origin main
    
    echo -e "${GREEN}✅ Daily commit process completed!${NC}"
}

# Run the main function
main "$@"
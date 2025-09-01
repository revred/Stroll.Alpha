#!/bin/bash

# Multi-Symbol Historical Data Generator
# Usage: ./generate-multi-symbol-data.sh [symbols] [start_date] [end_date] [batch_size]

set -e

# Configuration with defaults
SYMBOLS="${1:-XSP,VIX,GLD,USO}"  # Default: XSP, VIX, Gold, Oil (excluding SPX as it's already running)
START_DATE="${2:-2025-08-29}"
END_DATE="${3:-2018-01-01}"
BATCH_SIZE="${4:-5}"
DB_PATH="/c/Code/Stroll.Theta.DB/stroll_theta.db"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

echo -e "${BLUE}╔═══════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║              ${CYAN}STROLL.ALPHA MULTI-SYMBOL DATA GENERATOR${BLUE}            ║${NC}"
echo -e "${BLUE}║         ${YELLOW}Parallel historical options data generation${BLUE}          ║${NC}"
echo -e "${BLUE}╚═══════════════════════════════════════════════════════════════╝${NC}"
echo ""

echo -e "${CYAN}📊 Configuration:${NC}"
echo -e "${GREEN}  Symbols:${NC}     $SYMBOLS"
echo -e "${GREEN}  Database:${NC}   $DB_PATH"
echo -e "${GREEN}  Start Date:${NC} $START_DATE"
echo -e "${GREEN}  End Date:${NC}   $END_DATE"
echo -e "${GREEN}  Batch Size:${NC} $BATCH_SIZE"
echo ""

echo -e "${YELLOW}🚀 Starting multi-symbol data generation...${NC}"
echo ""

# Build the project first (if not locked by running SPX generation)
echo -e "${CYAN}Building multi-symbol generator...${NC}"
# We'll compile to a different output directory to avoid conflict
dotnet build src/Stroll.Alpha.Dataset -o /tmp/multi-symbol-build --verbosity quiet || {
    echo -e "${YELLOW}Warning: Build failed (likely due to running SPX process). Using existing binary...${NC}"
}

# Run the multi-symbol generator
echo -e "${CYAN}Starting generation for symbols: $SYMBOLS${NC}"

# Use the pre-built version or temp build
if [ -f "/tmp/multi-symbol-build/Stroll.Alpha.Dataset.exe" ]; then
    /tmp/multi-symbol-build/Stroll.Alpha.Dataset.exe generate-multi-symbol \
        --db-path "$DB_PATH" \
        --symbols "$SYMBOLS" \
        --start-date "$START_DATE" \
        --end-date "$END_DATE" \
        --batch-size "$BATCH_SIZE"
else
    # Fallback: compile and run in one step
    dotnet run --project src/Stroll.Alpha.Dataset generate-multi-symbol \
        --db-path "$DB_PATH" \
        --symbols "$SYMBOLS" \
        --start-date "$START_DATE" \
        --end-date "$END_DATE" \
        --batch-size "$BATCH_SIZE"
fi

echo -e "${GREEN}✅ Multi-symbol generation completed!${NC}"
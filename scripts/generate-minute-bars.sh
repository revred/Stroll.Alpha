#!/bin/bash

# Generate 1-Minute Bars Script for Stroll.Theta.Sixty
# Generates hourly Parquet files for 1-minute OHLCV bars
# Usage: ./generate-minute-bars.sh [symbols] [start_date] [end_date]

set -e

# Configuration
DB_PATH="/c/Code/Stroll.Theta.DB/stroll_theta.db"
OUTPUT_PATH="/c/code/Stroll.Theta.Sixty"
SYMBOLS="${1:-SPX,XSP,VIX,QQQ,GLD,USO}"
START_DATE="${2:-2025-08-29}"
END_DATE="${3:-2025-08-01}"  # Default: last month for testing

# Colors
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m'

echo -e "${BLUE}╔════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║              ${CYAN}STROLL.ALPHA 1-MINUTE BAR GENERATOR${BLUE}               ║${NC}"
echo -e "${BLUE}║            ${YELLOW}Generating Parquet files for Stroll.Theta.Sixty${BLUE}       ║${NC}"
echo -e "${BLUE}╚════════════════════════════════════════════════════════════════╝${NC}"
echo ""

echo -e "${CYAN}📊 Configuration:${NC}"
echo -e "${GREEN}  Database:${NC}    $DB_PATH"
echo -e "${GREEN}  Output:${NC}      $OUTPUT_PATH"
echo -e "${GREEN}  Symbols:${NC}     $SYMBOLS"
echo -e "${GREEN}  Start Date:${NC}  $START_DATE"
echo -e "${GREEN}  End Date:${NC}    $END_DATE"
echo ""

echo -e "${YELLOW}🔨 Building minute bar generator...${NC}"

# Build the project
cd /c/code/Stroll.Alpha
dotnet build src/Stroll.Alpha.Dataset --configuration Release --verbosity quiet

if [ $? -ne 0 ]; then
    echo -e "${RED}❌ Build failed${NC}"
    exit 1
fi

echo -e "${GREEN}✅ Build successful${NC}"
echo ""

echo -e "${YELLOW}🚀 Starting 1-minute bar generation...${NC}"
echo ""

# Run the minute bar generator
dotnet run --project src/Stroll.Alpha.Dataset --configuration Release -- generate-minute-bars \
    --db-path "$DB_PATH" \
    --output-path "$OUTPUT_PATH" \
    --symbols "$SYMBOLS" \
    --start-date "$START_DATE" \
    --end-date "$END_DATE"

if [ $? -eq 0 ]; then
    echo ""
    echo -e "${GREEN}🎉 Minute bar generation completed successfully!${NC}"
    echo -e "${CYAN}📁 Output location: $OUTPUT_PATH/bars/${NC}"
    
    # Show some stats
    echo ""
    echo -e "${CYAN}📊 Generated Files:${NC}"
    find "$OUTPUT_PATH/bars" -name "*.parquet" -type f 2>/dev/null | wc -l | xargs -I {} echo -e "${GREEN}  Parquet files: {}${NC}"
    
    if command -v du >/dev/null 2>&1; then
        total_size=$(du -sh "$OUTPUT_PATH/bars" 2>/dev/null | cut -f1 || echo "Unknown")
        echo -e "${GREEN}  Total size: $total_size${NC}"
    fi
else
    echo ""
    echo -e "${RED}❌ Minute bar generation failed${NC}"
    exit 1
fi
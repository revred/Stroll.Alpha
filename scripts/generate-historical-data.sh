#!/bin/bash

# Historical Data Generation Script
# Usage: ./generate-historical-data.sh [start_date] [end_date] [batch_size]

set -e

# Configuration
DB_PATH="/c/Code/Stroll.Theta.DB/stroll_theta.db"
START_DATE=${1:-"2025-08-29"}
END_DATE=${2:-"2018-01-01"}
BATCH_SIZE=${3:-5}

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}=== Stroll.Alpha Historical Data Generation ===${NC}"
echo -e "Database: ${DB_PATH}"
echo -e "Date range: ${START_DATE} to ${END_DATE}"
echo -e "Batch size: ${BATCH_SIZE} days"
echo ""

# Ensure database directory exists
mkdir -p "/c/Code/Stroll.Theta.DB"

# Initialize database schema
echo -e "${YELLOW}Initializing database schema...${NC}"
dotnet run --project src/Stroll.Alpha.Dataset init-schema "$DB_PATH"

# Generate historical data
echo -e "${YELLOW}Starting data generation...${NC}"
dotnet run --project src/Stroll.Alpha.Dataset generate-historical \
    --db-path "$DB_PATH" \
    --start-date "$START_DATE" \
    --end-date "$END_DATE" \
    --batch-size "$BATCH_SIZE"

echo -e "${GREEN}Historical data generation completed!${NC}"

# Run quality probe on recent data
echo -e "${YELLOW}Running data quality probe...${NC}"
dotnet run --project src/Stroll.Alpha.Probes -- --symbol SPX --date "$START_DATE" --depth 4

echo -e "${GREEN}All tasks completed successfully!${NC}"
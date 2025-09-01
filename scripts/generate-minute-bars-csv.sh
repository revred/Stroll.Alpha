#!/bin/bash

# Quick CSV-based 1-minute bars generator
# Usage: ./generate-minute-bars-csv.sh [symbol] [date] 

SYMBOL="${1:-SPX}"
DATE="${2:-2025-08-29}"
OUTPUT_DIR="/c/code/Stroll.Theta.Sixty/bars"
DB_PATH="/c/Code/Stroll.Theta.DB/stroll_theta.db"

echo "🚀 GENERATING 1-MINUTE BARS (CSV)"
echo "Symbol: $SYMBOL, Date: $DATE"

# Create output directory
mkdir -p "$OUTPUT_DIR/$(echo $DATE | tr '-' '/')/"

# Get base price from SQLite
BASE_PRICE=$(sqlite3 "$DB_PATH" "SELECT close FROM underlying_bars WHERE DATE(ts_utc) = '$DATE' AND symbol = '$SYMBOL' ORDER BY ts_utc DESC LIMIT 1" 2>/dev/null || echo "4800")

echo "Base price for $SYMBOL on $DATE: $BASE_PRICE"

# Generate 1-minute bars for each market hour (9:30-16:00)
for hour in {9..15}; do
    output_file="$OUTPUT_DIR/$(echo $DATE | tr '-' '/')/${SYMBOL}_${hour}.csv"
    
    if [ -f "$output_file" ]; then
        echo "✅ Hour $hour already exists"
        continue
    fi
    
    echo "📊 Generating hour $hour..."
    
    # Create CSV header
    echo "timestamp_utc,timestamp_et,symbol,open,high,low,close,volume,vwap" > "$output_file"
    
    # Generate 60 minutes of data for this hour
    for minute in {0..59}; do
        # Simple price simulation
        price_change=$(echo "scale=2; ($RANDOM - 16384) * $BASE_PRICE * 0.0001" | bc -l)
        current_price=$(echo "scale=2; $BASE_PRICE + $price_change" | bc -l)
        
        # OHLC simulation
        high=$(echo "scale=2; $current_price * 1.002" | bc -l)
        low=$(echo "scale=2; $current_price * 0.998" | bc -l)
        volume=$((1000 + RANDOM % 2000))
        
        # Timestamps
        if [ $hour -eq 9 ] && [ $minute -lt 30 ]; then
            continue  # Market opens at 9:30
        fi
        
        ts_et="$DATE $(printf "%02d:%02d:00" $hour $minute)"
        ts_utc="$DATE $(printf "%02d:%02d:00" $((hour + 4)) $minute)"  # Simple UTC conversion
        
        # Write CSV row
        echo "$ts_utc,$ts_et,$SYMBOL,$current_price,$high,$low,$current_price,$volume,$current_price" >> "$output_file"
    done
    
    echo "✅ Generated $output_file ($(wc -l < "$output_file") bars)"
done

echo "🎉 1-minute bar generation complete for $SYMBOL on $DATE"
# Phân tích nguyên nhân gốc Unmatched Orders - 20250904_141449

## Tóm tắt kết quả
- **Tổng số orders đã phân tích**: 22 (từ orders_ascii.csv)
- **Tổng số matched orders**: 22 (100% match trong dataset thực)
- **Tổng số unmatched orders**: 3 (synthetic examples để demo)

## Nguyên nhân chính được phát hiện

### 1. Missing Close Entry (missing_close)
**Mô tả**: Order đã được request/ack/fill nhưng không có corresponding close entry trong closed_trades.
**Ví dụ**: ORD-UNMATCHED-1
**Bằng chứng**: 
- Order tồn tại trong orders.csv với status REQUEST
- Không có entry_order_id hoặc exit_order_id tương ứng trong closed_trades_fifo_reconstructed.csv
- Timestamp của order nằm trong khoảng thời gian run window

### 2. ID Encoding Issues (id_encoding)
**Mô tả**: Order ID bị lỗi encoding, unicode normalization, hoặc whitespace leading/trailing.
**Ví dụ**: ORD-UNMATCHED-2
**Bằng chứng**:
- Order ID có thể chứa ký tự đặc biệt hoặc unicode
- Normalized comparison (strip, lowercase, unicode normalize) có thể tìm thấy match
- Log encoding có thể khác nhau giữa orders writer và close writer

### 3. Partial Fill Gap (partial_fill_gap)
**Mô tả**: Order có partial fills nhưng tổng filled size không bằng requested size.
**Ví dụ**: ORD-MISSING-CLOSE-3
**Bằng chứng**:
- Order size_requested = 1.0, nhưng partial fills chỉ sum = 0.5
- Gap có thể do network latency, race condition, hoặc logging truncation

## Nguyên nhân khác tiềm ẩn

### 4. Timestamp Outside Window
- Order timestamp nằm ngoài reconstruct window
- Close time sớm hơn order time (logic error)

### 5. File Truncation/Cutoff
- trade_closes.log bị terminate sớm
- CSV files bị truncated do disk space hoặc crash

### 6. Race Condition
- Concurrent writes giữa order logger và close logger
- Flush timing issues

## Recommended Actions

### Immediate (Priority 1)
1. **Enable additional flush on close writes**: Tăng flush frequency của close writer
2. **Add normalized ID comparison**: Implement fallback matching với normalized order IDs
3. **Increase telemetry flush frequency**: Reduce buffer time để giảm data loss risk

### Short-term (Priority 2)
1. **Enhance logging với ASCII-safe IDs**: Change logger để write IDs với ASCII-safe encoding
2. **Add fill aggregation logic**: Change reconstruct tool để aggregate partial fills và tolerate rounding
3. **Add detailed fill-size events logging**: More granular logging cho fill events

### Long-term (Priority 3)
1. **Implement automated unmatched detection**: Add monitoring cho unmatched orders trong production
2. **Add data integrity checks**: Pre-run và post-run validation cho data consistency
3. **Enhance error handling**: Better exception handling trong order/close writers

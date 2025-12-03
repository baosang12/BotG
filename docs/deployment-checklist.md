# Checklist Triển Khai PatternLayer Staging

## Trước Khi Trigger Pipeline

- [ ] Đảm bảo nhánh `phase1-safety-deployment` đã cập nhật code mới nhất.
- [ ] Xác nhận file `k8s/staging/patternlayer-configmap.yaml` đã chứa cấu hình PatternLayer mới.
- [ ] Kiểm tra `scripts/validate-deployment.ps1` không có lỗi cú pháp.
- [ ] Rà soát `docs/PATTERN_LAYER_CONFIGURATION_GUIDE.md` để chắc chắn thông số đồng nhất.

## Trong Azure DevOps Pipeline

1. **Deploy ConfigMap**
   - Bước `KubernetesManifest@0` phải thành công.
2. **Restart Deployment**
   - `kubectl rollout restart deployment/analysis-module` chạy không lỗi.
3. **Verify Deployment**
   - `kubectl rollout status` báo `successfully rolled out` trong 300 giây.
4. **Health Check Script**
   - Script PowerShell trả về mã thoát 0 và in đủ 4 bước kiểm tra.

## Sau Pipeline

- [ ] Vào Grafana dashboard `PatternLayer Performance` kiểm tra latency & score distribution.
- [ ] Kiểm tra Prometheus alert `pattern_layer_alerts` không bật.
- [ ] Gửi thông báo lên kênh Slack `#algo-staging-deployments` với trạng thái.
- [ ] Lập lịch thu thập metrics hàng ngày trong 14 ngày tiếp theo.

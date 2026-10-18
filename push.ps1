Remove-Item -Path check.py,fix_tray.py,fix_tray2.py,fix_tray3.py,fix4.py -Force -ErrorAction SilentlyContinue
git add -A
git commit -q -m "chore: 清理临时脚本"
git config --global --unset http.proxy
git config --global --unset https.proxy
git push origin main
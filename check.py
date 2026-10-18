s = open('D:\\自动调节亮度\\Project1\\BrightnessAuto\\Controls\\TrayMenuWindow.cs', encoding='utf-8').read()
i = s.lower().find('createpresenterstyle')
print(repr(s[i-10:i+420]))
print('---')
# 看 ColorTRANSPARENCY 是否是项目自定义扩展
import subprocess
@echo off
rem Startet das Skript mit Administratorrechten.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"')"

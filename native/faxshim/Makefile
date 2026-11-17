# Baut libfaxshim (Linux: libfaxshim.so, MSYS2/MinGW: faxshim.dll).
# Voraussetzung: spandsp + libtiff (pkg-config), z. B. "apt install libspandsp-dev".

CC ?= gcc
PKG ?= pkg-config
CFLAGS += -O2 -Wall -fvisibility=hidden $(shell $(PKG) --cflags spandsp libtiff-4)
LIBS += $(shell $(PKG) --libs spandsp libtiff-4) -lm

ifeq ($(OS),Windows_NT)
TARGET = faxshim.dll
LDFLAGS += -shared -static-libgcc
else
TARGET = libfaxshim.so
CFLAGS += -fPIC
LDFLAGS += -shared
endif

all: $(TARGET)

$(TARGET): faxshim.c faxshim.h
	$(CC) $(CFLAGS) -o $@ faxshim.c $(LDFLAGS) $(LIBS)

clean:
	rm -f libfaxshim.so faxshim.dll

.PHONY: all clean

VERBOSE_BIN ?= verbose
PREFIX ?= /usr/local

.PHONY: build test install uninstall clean

build:
	$(VERBOSE_BIN) src/vpm.vb -I src --no-run -o vpm
	@echo "[OK] Built native package manager -> ./vpm"

test: build
	VPM_BIN="$(CURDIR)/vpm" $(VERBOSE_BIN) test tests/test_vpm.vb -I src

install: build
	install -d "$(DESTDIR)$(PREFIX)/bin"
	install -m 755 ./vpm "$(DESTDIR)$(PREFIX)/bin/vpm"
	@echo "[INSTALLED] $(DESTDIR)$(PREFIX)/bin/vpm"

uninstall:
	rm -f "$(DESTDIR)$(PREFIX)/bin/vpm"

clean:
	rm -rf vpm *.ll src/*.ll tests/*.ll .vpkg build dist

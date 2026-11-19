// Every window this agent puts in front of a user, drawn by AppKit through osascript's JavaScript
// bridge. popup.rs hands it a card as JSON in argv[0] and reads back which button was pressed (its
// index) or, for a card with a password field, what was typed. With argv[1] == 'modeless' it is
// the progress window instead: it shows the card without waiting, replaces it with each JSON card
// that arrives on stdin, one a line, and exits when stdin closes. See popup.rs for why this is a
// script rather than a window in the agent's own process, and dialogs.rs for the cards themselves.
//
// The look is the Windows agent's popup.rs, point for point: the style guide's palette, Arial, the
// logo and small capitals over a bold title, a bordered list, a tinted callout, and pill buttons in
// a grey footer — the safe choice on the left, the primary on the right. Keep the two in step.
ObjC.import('Cocoa');
ObjC.import('stdlib');

var INK = 0x0F172A, SLATE = 0x475569, WHITE = 0xFFFFFF;
var ACTION = 0x0070C0, NEUTRAL_50 = 0xF8FAFC, NEUTRAL_100 = 0xF1F5F9, NEUTRAL_200 = 0xE2E8F0, NEUTRAL_300 = 0xCBD5E1;
// (text, tint, rule) — the Windows agent's Tone::colours.
var TONES = { info: [0x005270, 0xD8F3FD, 0x00AEEF], warning: [0x92400E, 0xFEF3C7, 0xF59E0B] };

var WIDTH = 480, PAD_X = 24, LOGO_HEIGHT = 34, LOGO_MAX_WIDTH = 160, BUTTON_HEIGHT = 38, FOOTER_PAD = 16;

function colour(hex) {
  return $.NSColor.colorWithSRGBRedGreenBlueAlpha(((hex >> 16) & 255) / 255, ((hex >> 8) & 255) / 255, (hex & 255) / 255, 1);
}

function font(size, bold) {
  var f = $.NSFont.fontWithNameSize(bold ? 'Arial-BoldMT' : 'ArialMT', size);
  if (f.isNil()) f = bold ? $.NSFont.boldSystemFontOfSize(size) : $.NSFont.systemFontOfSize(size);
  return f;
}

function styled(text, f, hex, kern) {
  var attributes = $.NSMutableDictionary.alloc.init;
  attributes.setObjectForKey(f, $.NSFontAttributeName);
  attributes.setObjectForKey(colour(hex), $.NSForegroundColorAttributeName);
  if (kern) attributes.setObjectForKey($(kern), $.NSKernAttributeName);
  // Built up rather than initWithString:attributes:, which the bridge does not expose.
  var string = $.NSMutableAttributedString.alloc.init;
  string.replaceCharactersInRangeWithString($.NSMakeRange(0, 0), text);
  string.setAttributesRange(attributes, $.NSMakeRange(0, string.length));
  return string;
}

// Lays a card out at WIDTH and returns its view, its height, and the controls the caller needs.
// `target` receives each button's press: (the button's tag is its index in card.buttons).
function layout(card, target) {
  var placed = [];
  function place(view, x, y, w, h) { placed.push({ view: view, x: x, y: y, w: w, h: h }); }

  // A wrapping label `width` wide, placed at (x, y) unless y is null; returns its height. Real text
  // fields rather than drawing, so VoiceOver reads every word.
  function text(value, f, hex, x, y, width, kern) {
    var field = $.NSTextField.wrappingLabelWithString('');
    field.attributedStringValue = styled(value, f, hex, kern);
    field.selectable = false;
    field.preferredMaxLayoutWidth = width;
    var height = Math.ceil(field.cell.cellSizeForBounds($.NSMakeRect(0, 0, width, 10000)).height);
    if (y !== null) place(field, x, y, width, height);
    return height;
  }
  // A label's own width, padding included, so a box sized from it never wraps the text inside.
  function width(value, f) {
    var field = $.NSTextField.labelWithString('');
    field.attributedStringValue = styled(value, f, INK);
    return Math.ceil(field.cell.cellSize.width);
  }
  // A filled, optionally bordered, rounded rectangle — NSBox's custom style, so no layer is needed.
  function box(fill, radius, border, x, y, w, h) {
    var b = $.NSBox.alloc.initWithFrame($.NSZeroRect);
    b.boxType = 4; // NSBoxCustom
    b.titlePosition = 0; // NSNoTitle
    b.fillColor = colour(fill);
    b.borderColor = colour(border === null ? fill : border);
    b.borderWidth = border === null ? 0 : 1;
    b.cornerRadius = radius;
    place(b, x, y, w, h);
  }

  var left = PAD_X, right = WIDTH - PAD_X, inner = right - left;
  var y = 20;
  var body = font(14, false), bodyBold = font(14, true), small = font(13, false);

  // The header: logo, then the small capitals and the title beside it.
  var logo = null, logoWidth = 0;
  if (card.logo) {
    var image = $.NSImage.alloc.initWithData($.NSData.alloc.initWithBase64EncodedStringOptions(card.logo, 0));
    if (!image.isNil() && image.size.height > 0) {
      logoWidth = Math.min(Math.round(LOGO_HEIGHT * image.size.width / image.size.height), LOGO_MAX_WIDTH);
      logo = $.NSImageView.imageViewWithImage(image);
      logo.imageScaling = 3; // NSImageScaleProportionallyUpOrDown
    }
  }
  var textLeft = logo ? left + logoWidth + 14 : left;
  var eyebrowFont = font(12, true), titleFont = font(19, true);
  var eyebrow = card.eyebrow.toUpperCase();
  var eyebrowHeight = text(eyebrow, eyebrowFont, SLATE, 0, null, right - textLeft, 0.7);
  var titleHeight = text(card.title, titleFont, INK, 0, null, right - textLeft);
  var block = eyebrowHeight + 2 + titleHeight;
  var header = Math.max(block, logo ? LOGO_HEIGHT : 0);
  if (logo) place(logo, left, y + (header - LOGO_HEIGHT) / 2, logoWidth, LOGO_HEIGHT);
  var top = y + (header - block) / 2;
  text(eyebrow, eyebrowFont, SLATE, textLeft, top, right - textLeft, 0.7);
  text(card.title, titleFont, INK, textLeft, top + eyebrowHeight + 2, right - textLeft);
  y += header;

  if (card.body) {
    y += 14;
    y += text(card.body, body, SLATE, left, y, inner);
  }

  // The bordered list, with "… and N more" after the last plain row, as display_rows has it.
  var rows = (card.rows || []).slice();
  if (card.more > 0) {
    var at = 0;
    rows.forEach(function (row, index) { if (!row.badge) at = index + 1; });
    rows.splice(at, 0, { label: '… and ' + card.more + ' more', badge: null });
  }
  if (rows.length > 0) {
    y += 14;
    var badgeFont = font(12, true);
    var measured = rows.map(function (row) {
      var badgeWidth = 0, badgeHeight = 0;
      if (row.badge) {
        badgeWidth = width(row.badge.text.toUpperCase(), badgeFont) + 14;
        badgeHeight = text(row.badge.text.toUpperCase(), badgeFont, INK, 0, null, 1000) + 8;
      }
      var labelWidth = inner - 28 - (badgeWidth ? badgeWidth + 12 : 0);
      var labelHeight = text(row.label, body, INK, 0, null, labelWidth);
      return { height: Math.max(labelHeight, badgeHeight) + 20, labelWidth: labelWidth, labelHeight: labelHeight, badgeWidth: badgeWidth, badgeHeight: badgeHeight };
    });
    var listHeight = measured.reduce(function (sum, m) { return sum + m.height; }, 0);
    box(WHITE, 8, NEUTRAL_200, left, y, inner, listHeight);
    var rowTop = y;
    rows.forEach(function (row, index) {
      var m = measured[index];
      if (index > 0) box(NEUTRAL_200, 0, null, left + 1, rowTop, inner - 2, 1);
      text(row.label, body, INK, left + 14, rowTop + (m.height - m.labelHeight) / 2, m.labelWidth);
      if (row.badge) {
        var tone = TONES[row.badge.tone] || TONES.info;
        var badgeTop = rowTop + (m.height - m.badgeHeight) / 2;
        box(tone[1], 4, null, right - 14 - m.badgeWidth, badgeTop, m.badgeWidth, m.badgeHeight);
        text(row.badge.text.toUpperCase(), badgeFont, tone[0], right - 14 - m.badgeWidth + 7, badgeTop + 4, m.badgeWidth - 14);
      }
      rowTop += m.height;
    });
    y += listHeight;
  }

  // The tinted panel: the rule colour showing down its left edge.
  if (card.callout) {
    y += 14;
    var tone = TONES[card.callout.tone] || TONES.info;
    var calloutLeft = left + 4 + 16, calloutWidth = right - 16 - calloutLeft;
    var calloutTitle = card.callout.title ? text(card.callout.title, bodyBold, INK, 0, null, calloutWidth) : 0;
    var calloutText = text(card.callout.text, body, SLATE, 0, null, calloutWidth);
    var calloutHeight = 28 + calloutTitle + calloutText;
    box(tone[2], 8, null, left, y, inner, calloutHeight);
    box(tone[1], 8, null, left + 4, y, inner - 4, calloutHeight);
    if (card.callout.title) text(card.callout.title, bodyBold, INK, calloutLeft, y + 14, calloutWidth);
    text(card.callout.text, body, SLATE, calloutLeft, y + 14 + calloutTitle, calloutWidth);
    y += calloutHeight;
  }

  // One line of what is happening, with "2 of 5" at its right, over a bar. Truncated rather than
  // wrapped, so the window keeps its height from one step to the next.
  if (card.progress) {
    y += 18;
    var progress = card.progress;
    var count = progress.total > 0 ? Math.min(progress.completed, progress.total) + ' of ' + progress.total : '';
    var countWidth = count ? width(count, small) : 0;
    var lineHeight = text('Xg', small, SLATE, 0, null, 1000);
    var status = $.NSTextField.labelWithString('');
    status.attributedStringValue = styled(progress.label, small, SLATE);
    status.cell.lineBreakMode = 4; // NSLineBreakByTruncatingTail
    place(status, left, y, inner - (countWidth ? countWidth + 12 : 0), lineHeight);
    if (count) text(count, small, SLATE, right - countWidth, y, countWidth);
    y += lineHeight + 8;
    box(NEUTRAL_100, 4, null, left, y, inner, 8);
    if (progress.total > 0 && progress.completed > 0) {
      box(ACTION, 4, null, left, y, Math.max(8, inner * Math.min(progress.completed, progress.total) / progress.total), 8);
    }
    y += 8;
  }

  var field = null;
  if (card.field) {
    y += 16;
    y += text(card.field.label, bodyBold, INK, left, y, inner) + 6;
    field = $.NSSecureTextField.alloc.initWithFrame($.NSZeroRect);
    field.font = body;
    field.placeholderString = card.field.placeholder || '';
    place(field, left, y, inner, 28);
    y += 28;
  }

  if (card.note) {
    y += 12;
    y += text(card.note, small, SLATE, left, y, inner);
  }

  // The footer: a grey band under a hairline, with the buttons in it. Each button is a borderless
  // NSButton over a box drawn as its pill, so it keeps Return, Space and VoiceOver.
  y += 20;
  var footerTop = y, footerHeight = FOOTER_PAD * 2 + BUTTON_HEIGHT;
  box(NEUTRAL_50, 0, null, 0, footerTop, WIDTH, footerHeight);
  box(NEUTRAL_200, 0, null, 0, footerTop, WIDTH, 1);
  var buttonFont = font(14, true);
  var pills = card.buttons.map(function (button) { return Math.max(width(button.label, buttonFont) + 36, 88); });
  var edge = right, buttons = [];
  for (var index = pills.length - 1; index >= 0; index--) {
    var spec = card.buttons[index];
    var pillLeft = index === 0 && pills.length > 1 ? left : edge - pills[index];
    var pillTop = footerTop + FOOTER_PAD;
    var fill = spec.style === 'primary' ? ACTION : WHITE;
    var border = spec.style === 'primary' ? null : spec.style === 'outline' ? ACTION : NEUTRAL_300;
    var labelColour = spec.style === 'primary' ? WHITE : spec.style === 'outline' ? ACTION : SLATE;
    box(fill, BUTTON_HEIGHT / 2, border, pillLeft, pillTop, pills[index], BUTTON_HEIGHT);
    var button = $.NSButton.alloc.initWithFrame($.NSZeroRect);
    button.bordered = false;
    button.attributedTitle = styled(spec.label, buttonFont, labelColour);
    button.tag = index;
    if (target) { button.target = target; button.action = 'press:'; }
    if (index === card.focused) button.keyEquivalent = '\r';
    place(button, pillLeft, pillTop, pills[index], BUTTON_HEIGHT);
    buttons[index] = button;
    edge = pillLeft - 12;
  }
  // Beside a single button only, as on Windows: with two, the left one is where it would go.
  if (card.footer_note && pills.length === 1) {
    var noteWidth = edge - left;
    var noteHeight = text(card.footer_note, small, SLATE, 0, null, noteWidth);
    text(card.footer_note, small, SLATE, left, footerTop + (footerHeight - noteHeight) / 2, noteWidth);
  }
  var height = footerTop + footerHeight;
  // The card's own white, underneath everything, so it does not depend on the window's background.
  box(WHITE, 0, null, 0, 0, WIDTH, height);
  placed.unshift(placed.pop());

  // AppKit's origin is the bottom left; everything above was laid out from the top.
  var content = $.NSView.alloc.initWithFrame($.NSMakeRect(0, 0, WIDTH, height));
  // Light whatever the Mac's own setting, because every colour above is: a password box in dark
  // mode would be drawn black on this white card.
  content.appearance = $.NSAppearance.appearanceNamed($.NSAppearanceNameAqua);
  placed.forEach(function (p) {
    p.view.frame = $.NSMakeRect(p.x, height - p.y - p.h, p.w, p.h);
    content.addSubview(p.view);
  });
  return { content: content, height: height, field: field, buttons: buttons };
}

// Titled, for the organisation's name, but with no close, minimise or zoom: a card is answered
// with its buttons. Always the light appearance, because every colour above is.
function window(card, laid) {
  var panel = $.NSPanel.alloc.initWithContentRectStyleMaskBackingDefer($.NSMakeRect(0, 0, WIDTH, laid.height), 1, 2, false);
  panel.appearance = $.NSAppearance.appearanceNamed($.NSAppearanceNameAqua);
  panel.backgroundColor = colour(WHITE);
  panel.title = card.window_title;
  [0, 1, 2].forEach(function (kind) {
    var control = panel.standardWindowButton(kind);
    if (!control.isNil()) control.hidden = true;
  });
  panel.contentView = laid.content;
  panel.initialFirstResponder = laid.field || laid.buttons[card.focused] || null;
  // NSPanel hides itself whenever its application deactivates unless told otherwise, which for a
  // background process is the moment the person clicks anywhere else.
  panel.hidesOnDeactivate = false;
  panel.collectionBehavior = 257; // CanJoinAllSpaces | FullScreenAuxiliary
  panel.center;
  return panel;
}

var onPress = function () {};

function run(argv) {
  var card = JSON.parse(argv[0]);
  var app = $.NSApplication.sharedApplication;
  app.setActivationPolicy($.NSApplicationActivationPolicyAccessory);
  ObjC.registerSubclass({
    name: 'KintsugiPopupTarget',
    methods: { 'press:': { types: ['void', ['id']], implementation: function (sender) { onPress(Number(sender.tag)); } } }
  });
  var target = $.KintsugiPopupTarget.alloc.init;

  if (argv[1] === 'modeless') return modeless(app, card, target);

  var laid = layout(card, target);
  var panel = window(card, laid);
  onPress = function (index) {
    // A password card answers only with a password: an empty box is not an answer.
    if (laid.field && laid.field.stringValue.js === '') { $.NSBeep(); return; }
    app.stopModalWithCode(index); // The bridge hands NSInteger properties back as strings, hence Number().
  };
  // In front of whatever is running and on every space, as the macOS password prompt always was: a
  // question from a background process is otherwise lost the first time another window takes focus.
  panel.level = 8; // NSModalPanelWindowLevel
  app.activateIgnoringOtherApps(true);
  var code = app.runModalForWindow(panel);
  panel.orderOut(null);
  return laid.field ? laid.field.stringValue.js : String(code);
}

// The progress window. It never takes the keyboard (orderFrontRegardless, not activation), so
// somebody typing keeps typing; Hide puts it away until the next card arrives, as on Windows.
function modeless(app, card, target) {
  var laid = layout(card, target);
  var panel = window(card, laid);
  onPress = function () { panel.orderOut(null); };
  panel.orderFrontRegardless;

  var input = $.NSFileHandle.fileHandleWithStandardInput, pending = '';
  ObjC.registerSubclass({
    name: 'KintsugiPopupInput',
    methods: {
      'received:': {
        types: ['void', ['id']],
        implementation: function (notification) {
          var data = notification.userInfo.objectForKey($.NSFileHandleNotificationDataItem);
          if (Number(data.length) === 0) $.exit(0); // The agent closed stdin, or is gone.
          pending += $.NSString.alloc.initWithDataEncoding(data, $.NSUTF8StringEncoding).js;
          var lines = pending.split('\n');
          pending = lines.pop();
          lines.forEach(function (line) {
            if (!line.trim()) return;
            var next = JSON.parse(line);
            laid = layout(next, target);
            var frame = panel.frame;
            panel.contentView = laid.content;
            panel.setContentSize($.NSMakeSize(WIDTH, laid.height));
            // Keep the top edge where it was, rather than AppKit's bottom-left origin.
            panel.setFrameTopLeftPoint($.NSMakePoint(frame.origin.x, frame.origin.y + frame.size.height));
            panel.orderFrontRegardless;
          });
          input.readInBackgroundAndNotify;
        }
      }
    }
  });
  var listener = $.KintsugiPopupInput.alloc.init;
  $.NSNotificationCenter.defaultCenter.addObserverSelectorNameObject(listener, 'received:', $.NSFileHandleReadCompletionNotification, input);
  input.readInBackgroundAndNotify;
  app.run;
}

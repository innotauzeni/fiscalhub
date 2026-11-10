import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:open_filex/open_filex.dart';
import 'package:path_provider/path_provider.dart';
import 'package:share_plus/share_plus.dart';

import 'kit.dart';

String _safe(String name) => name.replaceAll(RegExp(r'[^A-Za-z0-9._ -]'), '_');

/// Saves a downloaded file to the phone's temporary folder and opens it in whatever app handles that type (a PDF viewer, a spreadsheet app).
Future<void> openBytes(BuildContext context, Uint8List data, String fileName) async {
  try {
    final dir = await getTemporaryDirectory();
    final f = File('${dir.path}/${_safe(fileName)}');
    await f.writeAsBytes(data, flush: true);
    final r = await OpenFilex.open(f.path);
    if (r.type != ResultType.done && context.mounted) toast(context, r.message.isEmpty ? 'No app on this phone can open that file.' : r.message, error: true);
  } catch (e) {
    if (context.mounted) toast(context, 'Could not open the file: $e', error: true);
  }
}

/// Shares a downloaded file (WhatsApp, email, Drive...).
Future<void> shareBytes(BuildContext context, Uint8List data, String fileName, {String? text}) async {
  try {
    final dir = await getTemporaryDirectory();
    final f = File('${dir.path}/${_safe(fileName)}');
    await f.writeAsBytes(data, flush: true);
    await SharePlus.instance.share(ShareParams(files: [XFile(f.path)], text: text));
  } catch (e) {
    if (context.mounted) toast(context, 'Could not share the file: $e', error: true);
  }
}

Future<void> shareText(BuildContext context, String text, {String? subject}) async {
  try {
    await SharePlus.instance.share(ShareParams(text: text, subject: subject));
  } catch (e) {
    if (context.mounted) toast(context, 'Could not share: $e', error: true);
  }
}

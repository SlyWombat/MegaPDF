# Localisation glossary — English → Español (Latinoamérica)

The one vocabulary every platform's Spanish catalogue follows (#724). Same
English concept, same Spanish word on Windows, macOS, Linux, Android and iOS.
Add a row before you coin a new term; a term that exists here is not
re-translated per platform. The French file, `localisation-glossary.md`, is the
sibling of this one and its English column is the list of terms this file
must cover (`StringCatalogueTests.SpanishGlossaryCoversEveryEnglishTerm`).

Two variants, the way French has two: **es-419** (neutral Latin American
Spanish) is hand-written and is what the main table below gives;
**es-ES** (Spain) is derived from it by `tools/gen_strings.py es-es` and is the
last section. Every Spanish string is marked `ES-REVIEW #724` until Fable
approves it; nothing Spanish ships before that approval.

Conventions:

- Register is *tú* everywhere (*Abre*, *Guarda*, *Toca*, *Haz clic*). Apple,
  Google and Microsoft all use it in both variants; *usted* reads as a
  government form.
- Inverted marks open every question and exclamation: `¿…?`, `¡…!`.
- **No space of any kind before `: ; ? !`.** Spanish has no U+00A0 rule, so
  France's punctuation pass never runs on Spanish.
- Quotation marks are curly, with no inner spaces: `“{0}”`, in both variants.
  Never « » in Spanish.
- Sentence case for buttons and titles, as in the English.
- Product and brand names stay as they are: MegaPDF, Electric RV,
  Mega Woman, GitHub, Apache-2.0, Helvetica, Times, Courier.
- Language names are written in their own language: *English*,
  *Français (Canada)*, *Español (Latinoamérica)*, *Español (España)*.
- *MB* stays *MB*; numbers take the platform's culture formatting.
- Desktop says *haz clic*, phones say *toca*.
- Prefer *equipo* or *dispositivo* to *computadora*, so that the es-ES
  derivation has nothing to fix.
- *Correo electrónico* in both variants, never *email* or *e-mail*.

## Settings is the exception, and it is a variant split as well as a platform split

The issue said *Ajustes* on iOS and macOS "mirroring the French exception".
That is wrong for es-419 (design comment, D6). Each platform's own Settings app
is the authority, and measured:

| platform | es-419 | es-ES | reading |
|---|---|---|---|
| Android | **Configuración** | **Ajustes** | AOSP Settings app `settings_label`: `res/values-es-rUS/strings.xml` says "Configuración", `res/values-es/strings.xml` says "Ajustes" (read 2026-10-21 from android.googlesource.com, `platform/packages/apps/Settings`, branch main) |
| iOS | **Configuración** | **Ajustes** | iOS 26.5 simulator runtime on the Mac mini (read 2026-10-22): the Settings app's own `Preferences.app/es_419.lproj/InfoPlist.strings` has `CFBundleDisplayName` "Configuración", and `es.lproj/InfoPlist.strings` has "Ajustes". Read as files from the runtime, without booting a device; the app was not launched under `-AppleLanguages "(es-MX)"` / `"(es-ES)"` as the design suggested, so the title bar itself was not read |
| Windows | **Configuración** | **Configuración** | Windows says *Configuración* in both variants |
| macOS, Linux | no Settings string in the catalogue | | |

The split is applied **by key**, not by word, so a sentence such as *la
configuración de seguridad* is not rewritten: the keys are Android `settings`,
iOS `Settings` and `Settings…`, and Windows `ToolbarSettings` (es-ES keeps
*Configuración*). They live in `ES_ES_KEY_OVERRIDES` in `tools/gen_strings.py`.

## Signatures: two different things, and the words for each (#602)

The vocabulary is Adobe's, as in French: it is what the PDF specification calls
these objects and what other viewers say when they report a broken signature.

| meaning | English | Español (Latinoamérica) |
|---|---|---|
| the drawn, typed or photographed mark someone places on a page | **electronic signature**, and in the interface simply *signature*, *sign*, *your signature* | **firma electrónica**, y en la interfaz simplemente *firma*, *firmar*, *tus firmas* |
| a cryptographic signature backed by a certificate | **digital signature** | **firma digital** |
| the certificate and private key behind one | **digital ID** | **ID digital** (Adobe's own Spanish interface) |

Rules that follow, the French ones translated:

- The graphic feature keeps its friendly naming: *Firmas*, *firmar*, *tus firmas*.
- Nothing may suggest that placing a signature makes a document verifiable,
  tamper-evident, certified, legally binding or secure.
- *Firmar* must not sit beside *seguro*, *verificado*, *autenticado* or
  *protegido* in the same breath.
- Where a document's own cryptographic signature is meant, write **firma
  digital** on its first mention in that string; a later back-reference inside
  the same string may say just *la firma*.
- *Protegido* and *seguridad* belong to the password feature. Keep them there.
- **Never *cifrado*, *cifrar*, *encriptado* or *encriptar* for either kind of
  signature.** A digital signature encrypts nothing: it signs a hash with a
  private key, and the document stays readable to anyone.
- "Digital Encrypted Signature" is withdrawn (#601, #602) and must not appear.

## Main table

Every English term of `localisation-glossary.md`'s main table, in the same
order, with its es-419 rendering. A note that starts with an issue number
refers to the same row in the French file.

| English | Español (Latinoamérica) | Notas |
|---|---|---|
| Open | Abrir |  |
| Open a PDF | Abrir un PDF |  |
| Open PDF… | Abrir PDF… | los puntos suspensivos porque sigue un selector de archivos (atajo ⌘O del iPad) |
| Save | Guardar |  |
| Save As | Guardar como |  |
| Save a copy | Guardar una copia |  |
| Save a smaller copy for email | Guardar una copia reducida para correo electrónico |  |
| Your changes can't be undone after this | Tus cambios no se podrán deshacer después | #633, escritorio: el título de la pregunta previa a un guardado o a un cambio de contraseña que quita lo que Deshacer podía recuperar |
| "Make marks & signatures permanent when saving" is on, so this save makes your changes part of the page. MegaPDF won't ask again while this document is open. | La opción “Hacer permanentes las marcas y firmas al guardar” está activada, así que este guardado incorpora tus cambios a la página. MegaPDF no te lo volverá a preguntar mientras este documento esté abierto. | #633, cuerpo en Windows. Cita exactamente la etiqueta de `FlattenToggle.Header` |
| "Flatten when saving" is on, so this save bakes your changes into the page. MegaPDF won't ask again while this document is open. | La opción “Aplanar al guardar” está activada, así que este guardado incorpora tus cambios a la página. MegaPDF no te lo volverá a preguntar mientras este documento esté abierto. | #633, cuerpo en macOS/Linux. Cita exactamente `FlattenWhenSaving` |
| Setting, changing or removing a password saves the document and opens it again. After that, what you've done so far can't be undone. | Definir, cambiar o quitar una contraseña guarda el documento y lo vuelve a abrir. Después, no podrás deshacer lo que hayas hecho hasta ahora. | #633, ambos escritorios. Dice contraseña, nunca “cifrado” ni “protegido” |
| can't be undone (after a step that clears the undo history) | ya no se podrán deshacer / ya no podrás deshacer | #633: el verbo, nunca el sustantivo suelto “Deshacer” como botón; Cancelar es otro botón del mismo diálogo |
| Shrink | Reducir | etiqueta de la barra de herramientas |
| Shrink for email | Reducir para correo electrónico | nombre para accesibilidad |
| Print | Imprimir |  |
| printer | impresora |  |
| network | red | aviso de red local de macOS (Info.plist) |
| Scroll up, down, left, right | Desplazar hacia arriba, abajo, a la izquierda, a la derecha | #144, nombres accesibles de las flechas de una barra de desplazamiento |
| Page up, down, left, right | Una página hacia arriba, abajo, a la izquierda, a la derecha | #144, nombres accesibles del carril de una barra de desplazamiento |
| Undo | Deshacer |  |
| Redo | Rehacer |  |
| Cancel | Cancelar |  |
| Continue | Continuar |  |
| OK | Aceptar | es el botón del sistema en ambas variantes |
| Close | Cerrar |  |
| Done | Listo |  |
| Add | Agregar | Spain says *Añadir* (Apple's own Spanish, read in the iOS 26.5 Settings app's strings: `es_419` "agregar", `es` "añadir"; Android's es-ES too). Fable's call (#724): the derivation table has a row for each inflection of *agregar* the catalogues and store copy use (*Agregar* → *Añadir*, *agrega* → *añade*, *agregada* → *añadida*, and the rest), so es-ES reads *Añadir*; es-419 keeps *agregar*. Dave can override |
| Clear | Borrar |  |
| Delete | Eliminar |  |
| Delete (the key) | Supr (Windows) / Suprimir (Mac) | el nombre de la tecla en cada teclado |
| Remove | Quitar | de una biblioteca o de una página |
| Discard | Descartar |  |
| Share | Compartir | #378, la fila de la hoja de compartir en el menú Más |
| Share without saving | Compartir sin guardar | #378, deliberadamente no “Descartar”, porque no se descarta nada |
| Couldn't share. Try again. | No se pudo compartir. Inténtalo de nuevo. | #378, Android |
| share sheet | hoja de compartir | #378, solo textos de las tiendas; no es una cadena de la aplicación |
| Open with | Abrir con | #376, #377, solo textos de las tiendas |
| tab | pestaña | #348, el sustantivo |
| Close tab | Cerrar pestaña | #348 |
| Close window | Cerrar ventana | #348 |
| New window | Nueva ventana | #348 |
| Show Next Tab / Show Previous Tab | Mostrar la pestaña siguiente / Mostrar la pestaña anterior | #348, menú Ventana de macOS |
| {0}, tab | {0}, pestaña | #348, nombre accesible de una pestaña |
| {0}, unsaved changes, tab | {0}, cambios sin guardar, pestaña | #348, igual con el punto de cambios sin guardar |
| Move tab to new window | Mover la pestaña a una ventana nueva | #711, Windows |
| Tab {0} moved to another window | Pestaña {0} movida a otra ventana | #711, Windows: lo que dice Narrador |
| Move Tab to New Window | Mover pestaña a ventana nueva | #711, menú contextual de pestañas en Linux; el Mac muestra el texto de AppKit, nunca el nuestro |
| Box to tick / Box, ticked | Casilla para marcar / Casilla marcada | #2, Windows: lo que lee un lector de pantalla |
| Don't save | No guardar |  |
| Restore | Restaurar |  |
| Signatures | Firmas |  |
| Your signatures | Tus firmas |  |
| Sign | Firmar |  |
| Draw | Dibujar |  |
| Draw your signature | Dibuja tu firma | iOS only: *Tu firma* (the sheet title beside Cancel/Clear/Save has room for about eleven characters, #724 Fable); Windows and Android keep *Dibuja tu firma* |
| Type your signature | Escribe tu firma |  |
| Add from image… | Agregar desde una imagen… |  |
| Photos | Fotos | selector de iOS |
| Signature name | Nombre de la firma |  |
| My signature | Mi firma | nombre predeterminado |
| Remove from library | Quitar de la biblioteca |  |
| Remove from Recent | Quitar de recientes | #165 |
| Whiteout | Corrector | el sustantivo de la barra de herramientas |
| Cover | Cubrir | verbo de macOS |
| Cover an area with whiteout | Cubrir un área con corrector |  |
| Add text | Agregar texto |  |
| Edit text | Editar texto |  |
| Text | Texto |  |
| Size | Tamaño |  |
| Font | Fuente |  |
| Find | Buscar |  |
| Search | Buscar |  |
| More | Más | menú de desbordamiento; *Más opciones* donde la etiqueta es “More options” |
| Find in document | Buscar en el documento |  |
| No results | Sin resultados |  |
| Not found | No encontrado |  |
| {0} of {1} | {0} de {1} | contador de coincidencias y de páginas |
| Page {0} of {1} | Página {0} de {1} |  |
| Picture {0} of {1} | Imagen {0} de {1} | #593, barra de progreso al reducir para correo electrónico |
| Extract text | Extraer texto | #142, #168, #356 |
| Markdown | Markdown | sin cambios, como en las demás plataformas |
| Export as Markdown | Exportar como Markdown | #386, #409: *Exportar*, no *Guardar*, en todas las plataformas |
| Markdown document | Documento Markdown | #386 |
| Exported | Exportado | #386: deliberadamente no “Guardado” |
| Exported as Markdown | Exportado como Markdown | #386, Android |
| Couldn't export as Markdown. | No se pudo exportar como Markdown. | #386, Android |
| Exported {0}. | {0} exportado. | #386, macOS/Linux, con el nombre del archivo |
| Exporting… | Exportando… | #386 |
| Couldn't prepare the export. | No se pudo preparar la exportación. | #386 |
| Couldn't export the text. | No se pudo exportar el texto. | #386 |
| Could not export. | No se pudo exportar. |  |
| Next match | Siguiente coincidencia |  |
| Previous match | Coincidencia anterior |  |
| Zoom in | Acercar |  |
| Zoom out | Alejar |  |
| Fit width | Ajustar al ancho |  |
| Fit page | Página completa |  |
| Actual size | Tamaño real |  |
| Zoom and fit | Zoom y ajuste |  |
| Zoom level | Nivel de zoom |  |
| {0}% | {0} % | #144; espacio antes del símbolo de porcentaje, según la RAE y las convenciones del sistema |
| File, Edit, View, Tools | Archivo, Edición, Visualización, Herramientas |  |
| Ctrl+Plus, Ctrl+Minus | Ctrl+Más, Ctrl+Menos |  |
| Settings | Configuración | Windows y Android; ver “Configuración, la excepción” |
| Settings (iOS/macOS) | Configuración | iOS y macOS en es-419 (Ajustes en es-ES); ver la sección de Configuración |
| Options | Opciones |  |
| Checkbox mark | Marca de las casillas |  |
| Cross | Cruz |  |
| Check | Marca de verificación |  |
| Filled square | Cuadrado relleno |  |
| Theme | Tema |  |
| Use system setting | Según el sistema |  |
| Light | Claro |  |
| Dark | Oscuro |  |
| Language | Idioma |  |
| Use Windows setting | Según Windows |  |
| Reopen last file at startup | Volver a abrir el último archivo al iniciar |  |
| Make marks & signatures permanent when saving | Hacer permanentes las marcas y firmas al guardar |  |
| Flatten when saving | Aplanar al guardar |  |
| Check for updates at startup | Buscar actualizaciones al iniciar |  |
| About MegaPDF | Acerca de MegaPDF |  |
| Version {0} | Versión {0} |  |
| Free & open source · Apache-2.0 | Libre y de código abierto · Apache-2.0 |  |
| Copyright © 2026 ElectricRV.ca Corporation. All rights reserved. | © 2026 ElectricRV.ca Corporation. Todos los derechos reservados. |  |
| Special thanks to Mega Woman. | Un agradecimiento especial a Mega Woman. |  |
| Third-party notices | Avisos de terceros |  |
| Recent | Recientes |  |
| Drag a PDF here, or click Open | Arrastra un PDF aquí o haz clic en Abrir | escritorio: *haz clic* |
| Open. Fix. Save. Done. | Abrir. Corregir. Guardar. Listo. |  |
| Password | Contraseña |  |
| Password required | Se requiere contraseña |  |
| That password wasn't right — try again. | Esa contraseña no era correcta. Inténtalo de nuevo. |  |
| "{0}" is protected. Enter its password to open it. | “{0}” está protegido. Escribe su contraseña para abrirlo. |  |
| Owner password | Contraseña del propietario |  |
| Unlock | Desbloquear |  |
| Unlock with owner password… | Desbloquear con la contraseña del propietario… |  |
| ~~restricted (changes)~~ | ~~restringido (los cambios)~~ |  |
| The author of this document asked that it not be changed. You can still change it — MegaPDF will say so first. | El autor de este documento pidió que no se modifique. Aun así puedes modificarlo; MegaPDF te lo advertirá antes. |  |
| Change this document? / Rearrange these pages? / Copy these pages out? | ¿Modificar este documento? / ¿Reorganizar estas páginas? / ¿Extraer estas páginas? |  |
| The author of this document asked that its contents not be changed. | El autor de este documento pidió que no se modifique su contenido. |  |
| The author of this document asked that its pages not be reordered, removed or added to. | El autor de este documento pidió que no se reordenen, quiten ni agreguen sus páginas. |  |
| The author of this document asked that nothing be copied out of it. | El autor de este documento pidió que no se copie nada de él. |  |
| That's a request rather than a lock, so it's your call. The request stays in the document, and MegaPDF won't ask again while it's open. | Es una petición y no un bloqueo, así que la decisión es tuya. La petición permanece en el documento y MegaPDF no volverá a preguntar mientras esté abierto. |  |
| asked (of a permission bit) | pidió | un bit de permiso |
| Set password | Definir contraseña |  |
| Remove password | Quitar contraseña |  |
| crash recovery | recuperación tras un cierre inesperado |  |
| This form is built to be filled in with Adobe Reader. You can still view, print, save and share it here — only filling it in isn't possible. | Este formulario está hecho para llenarse con Adobe Reader. Aquí aún puedes verlo, imprimirlo, guardarlo y compartirlo; lo único que no se puede es llenarlo. | *Llenar* en es-419; *Rellenar* en es-ES, por derivación |
| This form needs Adobe Reader to fill in. | Este formulario necesita Adobe Reader para llenarse. |  |
| Get Adobe Reader | Obtener Adobe Reader |  |
| Save over the signed original? | ¿Guardar sobre el original firmado? |  |
| This document has a digital signature. Saving here will invalidate it. Save a copy to keep the signed original intact. | Este documento tiene una firma digital. Al guardar aquí se invalidará. Guarda una copia para conservar intacto el original firmado. |  |
| This document is certified as closed to changes. Saving here will invalidate that certification. Save a copy to keep the signed original intact. | Este documento está certificado como cerrado a cambios. Al guardar aquí se invalidará esa certificación. Guarda una copia para conservar intacto el original firmado. |  |
| Overwrite the signed original | Sobrescribir el original firmado |  |
| Save over the certified original? | ¿Guardar sobre el original certificado? |  |
| This document has a digital signature. Saving here will invalidate it. | Este documento tiene una firma digital. Al guardar aquí se invalidará. |  |
| This document is certified as closed to changes. Saving here will invalidate that certification. | Este documento está certificado como cerrado a cambios. Al guardar aquí se invalidará esa certificación. |  |
| Overwrite the certified original | Sobrescribir el original certificado |  |
| This document is also digitally signed; overwriting it will invalidate the signature too. | Este documento también tiene una firma digital; al sobrescribirlo también se invalidará la firma. |  |
| This document is also certified as closed to changes; overwriting it will invalidate that certification too. | Este documento también está certificado como cerrado a cambios; al sobrescribirlo también se invalidará esa certificación. |  |
| This document's digital signature doesn't carry over to the copy. | La firma digital de este documento no pasa a la copia. |  |
| ~~This document is signed~~ | ~~Este documento está firmado~~ |  |
| This document has a digital signature | Este documento tiene una firma digital |  |
| Saving will leave that signature invalid. MegaPDF rewrites the whole file when it saves, so the signature no longer describes the file it signed, and no way of saving can keep it valid. Save a copy to keep the signed original intact. | Al guardar, esa firma quedará inválida. MegaPDF reescribe el archivo completo al guardar, de modo que la firma ya no describe el archivo que firmó, y ninguna forma de guardar puede mantenerla válida. Guarda una copia para conservar intacto el original firmado. |  |
| This document is certified against changes | Este documento está certificado contra cambios |  |
| This document's digital signature certifies it — its author declared that it should not be changed at all. Saving will leave that signature invalid: MegaPDF rewrites the whole file when it saves, so the signature no longer describes the file it signed, and no way of saving can keep it valid. Save a copy to keep the signed original intact. | La firma digital de este documento lo certifica: su autor declaró que no debe modificarse en absoluto. Al guardar, esa firma quedará inválida: MegaPDF reescribe el archivo completo al guardar, de modo que la firma ya no describe el archivo que firmó, y ninguna forma de guardar puede mantenerla válida. Guarda una copia para conservar intacto el original firmado. |  |
| Signed on {0}. | Firmado el {0}. |  |
| The signer gave this reason: “{0}” | El firmante dio este motivo: “{0}” |  |
| This document has {0} digital signatures. | Este documento tiene {0} firmas digitales. |  |
| Also remove the signature, which will no longer be valid | Quitar también la firma, que ya no será válida |  |
| Left in, it travels with the file being saved and goes on claiming to describe a file it no longer describes, so MegaPDF and other readers report that file as signed. The document you opened is not changed either way — this reaches only the file being written. And a digital signature says a file has not changed since it was signed: it has never kept anyone from opening this document, so a file saved without it is readable by exactly the same people. | Si se deja, viaja con el archivo guardado y sigue afirmando que describe un archivo que ya no describe, de modo que MegaPDF y otros lectores indican que ese archivo está firmado. El documento que abriste no cambia en ningún caso: esto solo afecta al archivo que se escribe. Y una firma digital indica que un archivo no ha cambiado desde que se firmó: nunca ha impedido que alguien abra este documento, así que un archivo guardado sin ella pueden leerlo exactamente las mismas personas. | nunca “cifrado”: una firma digital no cifra nada |
| ~~The signature on the original doesn't carry over to this copy.~~ | ~~La firma del original no pasa a esta copia.~~ |  |
| ~~The original's digital signature doesn't carry over to this copy.~~ | ~~La firma digital del original no pasa a esta copia.~~ |  |
| The copy carries the original's digital signature, and it is no longer valid. | La copia lleva la firma digital del original, y ya no es válida. |  |
| Saved without the document's digital signature. The document you opened is unchanged. | Guardado sin la firma digital del documento. El documento que abriste no ha cambiado. |  |
| Save a copy, keeping the invalid signature | Guardar una copia conservando la firma inválida |  |
| Copy | Copiar |  |
| This page has no text to select. | Esta página no tiene texto que seleccionar. |  |
| The owner of this document doesn't allow copying its text. | El propietario de este documento no permite copiar su texto. |  |
| Start of selection / End of selection | Inicio de la selección / Fin de la selección |  |
| Next word / Previous word | Palabra siguiente / Palabra anterior |  |
| This field is read-only. | Este campo es de solo lectura. |  |
| Save as a copy, keeping the invalid signature | Guardar como copia conservando la firma inválida |  |
| Saved. The copy carries the document's digital signature, and it is no longer valid. | Guardado. La copia lleva la firma digital del documento, y ya no es válida. |  |
| Change this page? | ¿Modificar esta página? |  |
| Changing this page may slightly alter parts of it you haven't touched. | Modificar esta página podría alterar ligeramente partes que no has tocado. |  |
| Unsaved changes | Cambios sin guardar |  |
| Save changes to {0}? | ¿Guardar los cambios de {0}? |  |
| Your changes will be lost if you don't save them. | Si no los guardas, se perderán tus cambios. |  |
| Restore unsaved changes? | ¿Restaurar los cambios sin guardar? |  |
| This document has unsaved changes. They won't be in the shared copy unless you save first. | Este documento tiene cambios sin guardar. No estarán en la copia compartida a menos que guardes primero. |  |
| Couldn't open that file | No se pudo abrir ese archivo |  |
| This file is too large for MegaPDF to open. | Este archivo es demasiado grande para que MegaPDF pueda abrirlo. |  |
| Couldn't save | No se pudo guardar |  |
| Couldn't shrink | No se pudo reducir |  |
| Couldn't export | No se pudo exportar |  |
| Couldn't update | No se pudo actualizar |  |
| Save first | Guarda primero |  |
| Nothing to shrink | Nada que reducir |  |
| Shrinking wouldn't make this document any smaller. | Reducir no haría este documento más pequeño. | #6, ambos escritorios: el aviso cuando la copia reducida no sería más pequeña que el archivo en disco. *reducir* y *documento* como arriba También en los teléfonos (#593). |
| Smaller copy saved | Copia reducida guardada |  |
| Smaller copy ready | Copia reducida lista | #593, título de la hoja de resultado en los teléfonos |
| The document you have open is unchanged. | El documento que tienes abierto queda sin cambios. | #593, bajo “Antes/ahora” en la hoja de resultado de los teléfonos |
| Was {0} MB, now {1} MB. Saved as {2}, ready to email. | Antes: {0} MB; ahora: {1} MB. Guardada como {2}, lista para enviar por correo electrónico. |  |
| Saved | Guardado |  |
| Saving… | Guardando… |  |
| Opening… | Abriendo… |  |
| Checking the saved file… | Comprobando el archivo guardado… |  |
| Checking this page… | Comprobando esta página… |  |
| Applying… | Aplicando… |  |
| Searching… | Buscando… |  |
| Making a smaller copy… | Creando una copia reducida… |  |
| Preparing to print… | Preparando la impresión… |  |
| Restoring your edits… | Restaurando tus ediciones… |  |
| Do you want to save the changes made to the document “{0}”? | ¿Quieres guardar los cambios realizados en el documento “{0}”? |  |
| Don't Save | No guardar |  |
| Couldn't print | No se pudo imprimir |  |
| Couldn't make that change | No se pudo hacer ese cambio |  |
| The change could not be made. | No se pudo hacer el cambio. |  |
| Couldn't restore your edits | No se pudieron restaurar tus ediciones |  |
| Couldn't search this document. | No se pudo buscar en este documento. |  |
| Couldn't move this signature | No se pudo mover esta firma |  |
| PDF document | Documento PDF |  |
| Markdown document | Documento Markdown |  |
| {name} - edited | {name} - editado |  |
| {name} - smaller | {name} - reducido |  |
| Scanned image | Imagen escaneada |  |
| Esc cancels | Esc cancela |  |
| Update | Actualizar |  |
| Restart now | Reiniciar ahora |  |
| A new version of MegaPDF is available ({0}). | Hay una nueva versión de MegaPDF disponible ({0}). |  |
| Make MegaPDF your PDF app? | ¿Quieres que MegaPDF sea tu aplicación de PDF? |  |
| Choose default apps | Elegir aplicaciones predeterminadas |  |
| Something went wrong. | Algo salió mal. |  |
| Tap the page where the text should go | Toca la página donde debe ir el texto | phones: *toca* |
| Tap the page where the signature should go | Toca la página donde debe ir la firma |  |
| Signature added | Firma agregada |  |
| Signature {0} | Firma {0} |  |
| Redact | Censurar | Adobe's Spanish; never *redactar*, a false friend meaning "to write" |
| Redaction | Censura |  |
| Marked for redaction | Marcado para censura |  |
| Selects this mark. | Selecciona esta marca. |  |
| Remove content from the file | Quitar contenido del archivo |  |
| Cover an area — this doesn't remove what's under it | Cubre un área; esto no quita lo que hay debajo |  |
| Whiteout covers — it doesn't remove | El corrector cubre; no quita nada |  |
| Save as a copy | Guardar como copia |  |
| Overwrite the original | Sobrescribir el original |  |
| -redacted | -censurado | sufijo del nombre de archivo |
| {0} areas redacted: {1} | {0} áreas censuradas: {1} |  |
| {0} off | {0} desactivado |  |
| Nothing was removed | No se quitó nada |  |
| About MegaPDF | Acerca de MegaPDF |  |
| Version {0} ({1}) | Versión {0} ({1}) |  |
| Third-Party Notices… | Avisos de terceros… |  |
| The notices file is missing from this build. | Falta el archivo de avisos en esta compilación. |  |
| Window | Ventana |  |
| Help | Ayuda |  |
| Minimize | Minimizar |  |
| Zoom | Zoom |  |
| Hide {0} | Ocultar {0} |  |
| Hide Others | Ocultar otros |  |
| Show All | Mostrar todo |  |
| Quit {0} | Salir de {0} |  |
| Services | Servicios | confirmar con Edit.loctable de TextEdit antes de fijar, como en #191 |
| Show in Finder | Mostrar en el Finder |  |
| Show in Files | Mostrar en Archivos |  |
| Save to Files | Guardar en Archivos | #593, hoja de resultado de iOS; el nombre que Apple da a la app Archivos, como en la fila anterior |
| {0}, in {1} | {0}, en {1} |  |
| Not found · {0} | No encontrado · {0} |  |
| iCloud Drive | iCloud Drive |  |
| On My iPhone / On My iPad | En mi iPhone / En mi iPad |  |
| Downloads | Descargas |  |
| Couldn't show {0} in the Finder. | No se pudo mostrar {0} en el Finder. |  |
| Built for Adobe Reader | Hecho para Adobe Reader |  |
| This form is designed to be filled in using Adobe Reader. MegaPDF can't fill it in, but you can still view, save, share and export it. | Este formulario está diseñado para llenarse con Adobe Reader. MegaPDF no puede llenarlo, pero aún puedes verlo, guardarlo, compartirlo y exportarlo. |  |
| Get Adobe Reader | Obtener Adobe Reader |  |
| This form can only be filled in using Adobe Reader. | Este formulario solo se puede llenar con Adobe Reader. |  |
| Form field | Campo de formulario |  |
| This will invalidate the digital signature | Esto invalidará la firma digital |  |
| MegaPDF rewrites the whole file when it saves, so the document's digital signature will no longer verify once you save here. Save a copy instead to leave the signed original untouched. | MegaPDF reescribe el archivo completo al guardar, así que la firma digital del documento ya no se podrá verificar una vez que guardes aquí. Guarda una copia para dejar intacto el original firmado. |  |
| This document does not allow changes | Este documento no permite cambios |  |
| This document was certified not to be modified — its author declared that no changes are allowed at all. Saving here will break that certification as well as the digital signature. Save a copy instead to leave the signed original untouched. | Este documento se certificó para que no se modifique: su autor declaró que no se permite ningún cambio. Al guardar aquí se romperá esa certificación, además de la firma digital. Guarda una copia para dejar intacto el original firmado. |  |
| Saved. The digital signature doesn't carry over to a copy. | Guardado. La firma digital no pasa a una copia. |  |
| Reading mode | Modo de lectura |  |
| Enter Full Screen / Exit Full Screen | Entrar en pantalla completa / Salir de pantalla completa |  |
| Reading mode on / Reading mode off | Modo de lectura activado / Modo de lectura desactivado |  |
| Exit | Salir |  |
| Exit reading mode | Salir del modo de lectura |  |
| Reading controls | Controles de lectura |  |
| Previous page / Next page | Página anterior / Página siguiente |  |
| Go to page | Ir a la página |  |
| Page colours | Colores de la página |  |
| Normal / Sepia / Night | Normal / Sepia / Noche |  |
| Night inverts the page, pictures included. | El modo noche invierte la página, incluidas las imágenes. |  |
| Open documents in reading mode | Abrir los documentos en modo de lectura |  |
| Page %lld of %lld | Página %1$lld de %2$lld |  |
| Page | Página |  |
| Go | Ir |  |
| Settings… | Configuración… | iOS y macOS; en es-ES *Ajustes…* en iOS |
| Reading | Lectura |  |
| Hide the tools and read the page (Ctrl+H) | Oculta las herramientas y lee la página (Ctrl+H) |  |
| Copy (selected text) | Copiar |  |
| Select all / Select All | Seleccionar todo | el inglés distingue “Select all” y “Select All” (título del menú del Mac); en español es la misma frase |
| The author of this document asked that nothing be copied out of it. (Copy unavailable) | El autor de este documento pidió que no se copie nada de él. (Copiar no disponible) |  |
| Selected: {0} (announced) | Seleccionado: {0} | #710, las aplicaciones de escritorio: lo que dice el lector de pantalla cuando una selección de texto queda fija; {0} son los primeros 80 caracteres. Solo se oye. Sin espacio antes de los dos puntos |
| Copied (announced) | Copiado | #710, lo que se oye tras copiar con éxito; igual que la confirmación de Android (*select_copied*) |
| {0} selected (Android page-grid title while pages are ticked) | Selección: {0} | #724, la barra de selección de la vista de páginas en Android. *Seleccionadas: {0}* daba dos líneas a esa anchura en es-419 y es-ES (“Seleccionadas:” sobre el número), y la forma con el número delante (*{0} seleccionadas*) no concuerda con 1 y casi no cabe con dos cifras; *Selección* no tiene género ni número. Dos puntos sin espacio antes |

## Español (España)

Spain's Spanish is not translated separately: `tools/gen_strings.py es-es`
derives it from the Latin American catalogues with the table below. There is
no punctuation pass. Add a row here **and** to `ES_419_TO_ES_ES` in the
script when a new Latin American term needs a Spain counterpart; never edit a
derived file. Whole words only, so *videoteca* and *llenarlos* are left alone.

| Español (Latinoamérica) | Español (España) | ámbito |
|---|---|---|
| las computadoras | los ordenadores | any string |
| una computadora | un ordenador | any string |
| la computadora | el ordenador | any string |
| esta computadora | este ordenador | any string |
| computadoras | ordenadores | any string |
| computadora | ordenador | any string |
| Computadora | Ordenador | any string |
| celulares | móviles | any string |
| celular | móvil | any string |
| Celular | Móvil | any string |
| videos | vídeos | any string |
| video | vídeo | any string |
| Videos | Vídeos | any string |
| Video | Vídeo | any string |
| el mouse | el ratón | any string |
| mouse | ratón | any string |
| Presiona | Pulsa | any string |
| presiona | pulsa | any string |
| Presionar | Pulsar | any string |
| presionar | pulsar | any string |
| Llenar | Rellenar | any string |
| llenar | rellenar | any string |
| Llena | Rellena | any string |
| llena | rellena | any string |
| llenado | rellenado | any string |
| llenada | rellenada | any string |
| llenarse | rellenarse | any string |
| llenarlo | rellenarlo | any string |
| presionada | pulsada | any string |
| presionado | pulsado | any string |
| ingresaste | introdujiste | the store copy's "los valores que ingresaste" |
| ingresados | introducidos | the store copy's "los valores ingresados" |
| Agregar | Añadir | any string |
| agregar | añadir | any string |
| AGREGAR | AÑADIR | the all-caps store heading AGREGAR TEXTO |
| Agregando | Añadiendo | any string |
| Agrega | Añade | any string |
| agrega | añade | any string |
| agregas | añades | any string |
| agregues | añadas | any string |
| agreguen | añadan | any string |
| Agrégalo | Añádelo | any string |
| agregó | añadió | any string |
| agregará | añadirá | any string |
| agregaron | añadieron | any string |
| agregadas | añadidas | any string |
| agregada | añadida | any string |
| agregado | añadido | any string |
| Sofía Ramírez | Lucía Fernández | the demo person in the store captures (D7) |
| demo-es | demo-es-ES | the demo document the phones open: demo-es.pdf and demo-es-ES.pdf |
| arrendamiento | alquiler | the demo document's rental word, so the captures search a word Spain's page contains |
| Configuración | Ajustes | by key only: Android `settings`, iOS `Settings` and `Settings…`; Windows keeps *Configuración* |

The demo documents the store captures show are the exception: their text is
written per listing language, not derived. `tools/gen_test_fixtures.py`
(`DEMO_TEXT["es"]` for es-419, `DEMO_TEXT["es-ES"]`) carries both pages:
Latin America's is a *contrato de arrendamiento* in metric units and `$`;
Spain's is a *contrato de alquiler* in metric units and `€`. Same layout, same
demo person per language (Sofía Ramírez, Lucía Fernández; Dave, 2026-10-21),
and the lines the screenshot poses key on — the rental word three times,
*secciones 1 a 4* on one line — are the same in both.
